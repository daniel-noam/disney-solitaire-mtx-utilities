using System.Collections.Generic;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Wraps "git update-index --skip-worktree", which is the counterpart to the exclude file:
    /// <list type="bullet">
    /// <item>exclude  - for files git does NOT track, so they never get added.</item>
    /// <item>skip-worktree - for files git DOES track, where you want to keep local modifications
    /// and have git stop reporting them and stop overwriting them on checkout.</item>
    /// </list>
    /// The classic case is a personal package in Packages/manifest.json: it must stay tracked (the file
    /// is shared) but your local edit has to survive branch switches.
    /// </summary>
    internal static class GitSkipWorktree
    {
        /// <summary>
        /// Paths per git invocation. A folder selection can expand to thousands of tracked files, which
        /// would blow past the OS argument-length limit in one command.
        /// </summary>
        private const int PathsPerBatch = 200;

        /// <summary>Repository-relative paths currently flagged skip-worktree, sorted.</summary>
        public static List<string> List(GitRepositoryInfo repository)
        {
            var skipped = new List<string>();
            if (!repository.IsValid) return skipped;

            GitCommandRunner.Result result = GitCommandRunner.Run(repository.RepositoryRoot, "ls-files", "-v");
            if (!result.Success)
            {
                Debug.LogError($"<b>[Git Exclude]</b> Could not list skip-worktree files: {result.FailureMessage}");
                return skipped;
            }

            // "git ls-files -v" prefixes each path with a status letter and a space. Uppercase 'S' is
            // skip-worktree; lowercase letters mean assume-unchanged, which is a different mechanism.
            foreach (string rawLine in result.Output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length < 3 || line[0] != 'S' || line[1] != ' ') continue;

                skipped.Add(line.Substring(2));
            }

            skipped.Sort();
            return skipped;
        }

        /// <summary>Sets or clears the skip-worktree bit on the given repository-relative paths.</summary>
        /// <returns>How many paths were successfully updated.</returns>
        public static int Set(GitRepositoryInfo repository, IList<string> repositoryPaths, bool skip)
        {
            if (!repository.IsValid || repositoryPaths == null || repositoryPaths.Count == 0) return 0;

            string flag = skip ? "--skip-worktree" : "--no-skip-worktree";
            int updated = 0;

            for (int start = 0; start < repositoryPaths.Count; start += PathsPerBatch)
            {
                var arguments = new List<string> { "update-index", flag, "--" };

                int end = Mathf.Min(start + PathsPerBatch, repositoryPaths.Count);
                for (int i = start; i < end; i++) arguments.Add(repositoryPaths[i]);

                GitCommandRunner.Result result = GitCommandRunner.Run(repository.RepositoryRoot, arguments.ToArray());
                if (result.Success)
                {
                    updated += end - start;
                }
                else
                {
                    Debug.LogError($"<b>[Git Exclude]</b> git {flag} failed: {result.FailureMessage}");
                }
            }

            return updated;
        }

        // ---- Pause / resume ----------------------------------------------------------------------

        /// <summary>
        /// Clears the skip-worktree bit but records the file as paused, so it can be restored later.
        /// Use this to let git deliver upstream changes to a file you normally keep pinned.
        /// </summary>
        /// <returns>How many paths were paused.</returns>
        public static int Pause(GitRepositoryInfo repository, IList<string> repositoryPaths)
        {
            if (Set(repository, repositoryPaths, false) == 0) return 0;

            var paused = new List<GitSkipWorktreePauseStore.PausedEntry>(GitSkipWorktreePauseStore.Load(repository));
            var alreadyPaused = new HashSet<string>();
            foreach (var entry in paused) alreadyPaused.Add(entry.Path);

            int added = 0;
            foreach (string path in repositoryPaths)
            {
                if (!alreadyPaused.Add(path)) continue;

                // Hashed now so resuming can tell whether the file was rewritten while unprotected.
                string hash = GitSkipWorktreePauseStore.ComputeContentHash(repository, path);
                paused.Add(new GitSkipWorktreePauseStore.PausedEntry(path, hash));
                added++;
            }

            if (added > 0) GitSkipWorktreePauseStore.Save(repository, paused);
            return added;
        }

        /// <summary>Re-applies the skip-worktree bit to paused files and drops them from the paused list.</summary>
        /// <returns>How many paths were resumed.</returns>
        public static int Resume(GitRepositoryInfo repository, IList<string> repositoryPaths)
        {
            if (repositoryPaths == null || repositoryPaths.Count == 0) return 0;

            var resuming = new HashSet<string>(repositoryPaths);
            var remaining = new List<GitSkipWorktreePauseStore.PausedEntry>();
            var changedWhilePaused = new List<string>();

            foreach (var entry in GitSkipWorktreePauseStore.Load(repository))
            {
                if (!resuming.Contains(entry.Path))
                {
                    remaining.Add(entry);
                    continue;
                }

                string hashNow = GitSkipWorktreePauseStore.ComputeContentHash(repository, entry.Path);
                if (entry.ContentHash.Length > 0 && hashNow.Length > 0 && hashNow != entry.ContentHash)
                    changedWhilePaused.Add(entry.Path);
            }

            int updated = Set(repository, repositoryPaths, true);
            if (updated == 0) return 0;

            GitSkipWorktreePauseStore.Save(repository, remaining);

            if (changedWhilePaused.Count > 0)
            {
                // The whole point of pausing is to receive upstream edits, so this is information rather
                // than an error - but the user needs to know their local change may be gone.
                Debug.LogWarning($"<b>[Git Exclude]</b> {changedWhilePaused.Count} file(s) changed while paused " +
                                 $"and are now pinned again in that new state:\n- {string.Join("\n- ", changedWhilePaused.ToArray())}" +
                                 "\nCheck your local edits are still present.");
            }

            return updated;
        }

        /// <summary>
        /// The paused list with entries that git already reports as skipped removed - someone may have
        /// re-applied the bit outside this tool, which makes the pause record stale.
        /// </summary>
        public static List<GitSkipWorktreePauseStore.PausedEntry> LoadPaused(
            GitRepositoryInfo repository, ICollection<string> currentlySkipped)
        {
            List<GitSkipWorktreePauseStore.PausedEntry> paused = GitSkipWorktreePauseStore.Load(repository);
            if (currentlySkipped == null || currentlySkipped.Count == 0) return paused;

            var reconciled = new List<GitSkipWorktreePauseStore.PausedEntry>();
            foreach (var entry in paused)
            {
                if (!currentlySkipped.Contains(entry.Path)) reconciled.Add(entry);
            }

            if (reconciled.Count != paused.Count) GitSkipWorktreePauseStore.Save(repository, reconciled);
            return reconciled;
        }

        /// <summary>
        /// Maps a Project-window selection to the tracked files it covers. Folders expand to their
        /// contents, untracked files drop out, and .meta siblings are included when tracked - skip-worktree
        /// only works on tracked files, so a raw selection would otherwise fail with "does not exist in
        /// the index".
        /// </summary>
        public static List<string> ExpandToTrackedFiles(GitRepositoryInfo repository, IEnumerable<string> assetPaths)
        {
            var tracked = new List<string>();
            if (!repository.IsValid || assetPaths == null) return tracked;

            var pathspecs = new List<string>();
            foreach (string assetPath in assetPaths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;

                string repositoryPath = repository.ToRepositoryPath(assetPath);
                pathspecs.Add(repositoryPath);
                pathspecs.Add(repositoryPath + ".meta");
            }

            if (pathspecs.Count == 0) return tracked;

            var seen = new HashSet<string>();

            for (int start = 0; start < pathspecs.Count; start += PathsPerBatch)
            {
                var arguments = new List<string> { "ls-files", "--" };

                int end = Mathf.Min(start + PathsPerBatch, pathspecs.Count);
                for (int i = start; i < end; i++) arguments.Add(pathspecs[i]);

                GitCommandRunner.Result result = GitCommandRunner.Run(repository.RepositoryRoot, arguments.ToArray());
                if (!result.Success)
                {
                    Debug.LogError($"<b>[Git Exclude]</b> Could not resolve tracked files: {result.FailureMessage}");
                    continue;
                }

                foreach (string rawLine in result.Output.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Length > 0 && seen.Add(line)) tracked.Add(line);
                }
            }

            return tracked;
        }
    }
}
