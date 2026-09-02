using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Keeps this toolset and the files it writes out of Git.
    /// <para>
    /// Collects this toolset's own folder plus every path declared with
    /// <see cref="GitExcludeProviderAttribute"/>, asks Git which of them are actually hidden, and adds
    /// the ones that are not. Asking Git rather than trusting the exclude file matters: an entry can be
    /// silently overridden by .gitignore, or be powerless because the file is already tracked, and both
    /// of those look exactly like success from the file's contents alone.
    /// </para>
    /// <para>
    /// The pass runs once per editor session and says nothing unless it changed the file. Findings it
    /// cannot act on - a tracked path, or an entry .gitignore re-includes - are reported only from the
    /// button in <see cref="GitExcludeManager"/>, so unattended loads never nag.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class GitExcludeReminder
    {
        private const string SessionKey = "CustomTools_ExcludeReminderShown";
        private const string AutoRegisterPrefsKey = "CustomTools_GitExcludeAutoRegister";
        private const string MenuPath = "Utilities/Git Local Exclude Manager";
        private const string EditorFolderName = "Editor";

        /// <summary>
        /// Whether the load-time pass writes missing entries. Off makes editor loads leave the exclude
        /// file alone entirely; the window button still registers on request either way. Per user rather
        /// than per project - it is a preference about this machine's checkouts.
        /// </summary>
        public static bool AutoRegisterEnabled
        {
            get => EditorPrefs.GetBool(AutoRegisterPrefsKey, true);
            set => EditorPrefs.SetBool(AutoRegisterPrefsKey, value);
        }

        static GitExcludeReminder()
        {
            EditorApplication.delayCall += RunOncePerSession;
        }

        private static void RunOncePerSession()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            Run(false);
        }

        /// <summary>
        /// One full pass. <paramref name="verbose"/> marks it as an explicit request: it reports
        /// everything it found, including what it cannot fix, and registers missing paths whether or not
        /// <see cref="AutoRegisterEnabled"/> is on. Without it the pass is silent apart from a line when
        /// it actually writes to the exclude file.
        /// </summary>
        internal static void Run(bool verbose)
        {
            GitRepositoryInfo repository = GitRepositoryInfo.Locate();
            if (!repository.IsValid)
            {
                if (verbose)
                    Debug.LogWarning("<b>[Git Exclude]</b> No Git repository found in this project folder or " +
                                     "any folder above it, so there is nothing to exclude from.");
                return;
            }

            List<string> paths = CollectPaths();
            if (paths.Count == 0) return;

            var entryOwners = new Dictionary<string, string>();
            List<string> entries = BuildEntries(repository, paths, entryOwners);

            List<GitExcludeVerifier.Verdict> verdicts = GitExcludeVerifier.Verify(repository, entries);

            // Verify returns nothing when git is not on PATH, which is common for a Unity launched from
            // Finder. Fall back to reading the exclude file rather than deciding everything is unprotected.
            if (verdicts.Count == 0)
            {
                RunWithoutGit(repository, paths, verbose);
                return;
            }

            Apply(repository, verdicts, entryOwners, verbose);
        }

        private static void Apply(GitRepositoryInfo repository, List<GitExcludeVerifier.Verdict> verdicts,
                                  Dictionary<string, string> entryOwners, bool verbose)
        {
            var tracked = new List<string>();
            var unprotectedOwners = new List<string>();

            foreach (GitExcludeVerifier.Verdict verdict in verdicts)
            {
                if (verdict.Status == GitExcludeVerifier.Status.Tracked)
                {
                    tracked.Add(verdict.Explanation);
                }
                else if (verdict.Status == GitExcludeVerifier.Status.NotIgnored &&
                         entryOwners.TryGetValue(verdict.RepositoryPath, out string owner) &&
                         !unprotectedOwners.Contains(owner))
                {
                    unprotectedOwners.Add(owner);
                }
            }

            int added = 0;
            var stillVisible = new List<string>();

            // An explicit check is also a request to fix what it can, so it registers regardless of the
            // toggle - the toggle only governs what happens unattended.
            if (unprotectedOwners.Count > 0 && (verbose || AutoRegisterEnabled))
            {
                added = GitExcludeManager.AddPathsToExclude(unprotectedOwners);

                // Worth a line even on a silent pass: something just edited the user's .git/info/exclude.
                if (added > 0)
                {
                    Debug.Log($"<b>[Git Exclude]</b> Registered {added} tool path(s) in the local exclude file:\n" +
                              $"- {Join(unprotectedOwners)}");
                }

                if (verbose) stillVisible = CollectStillVisible(repository, unprotectedOwners);
            }

            if (!verbose) return;

            if (tracked.Count > 0)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> {tracked.Count} tool path(s) are tracked by Git, so the " +
                                 $"exclude file cannot hide them:\n- {Join(tracked)}\n" +
                                 $"Use the Skip Worktree tab in <b>{MenuPath}</b> to keep local edits to them. " +
                                 "That also stops you receiving upstream changes, so it is left to you.");
            }

            if (stillVisible.Count > 0)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> {stillVisible.Count} entr(ies) are in the exclude file but " +
                                 $"Git still shows them:\n- {Join(stillVisible)}\n" +
                                 "A pattern in .gitignore outranks the exclude file, so these cannot be hidden " +
                                 "this way - commit them once, or leave them showing as untracked.");
            }

            if (tracked.Count == 0 && stillVisible.Count == 0 && added == 0)
                Debug.Log($"<b>[Git Exclude]</b> All {verdicts.Count} tool entr(ies) are hidden from Git.");
        }

        /// <summary>Re-checks what was just written: an entry can be in the file and still do nothing.</summary>
        private static List<string> CollectStillVisible(GitRepositoryInfo repository, List<string> paths)
        {
            var stillVisible = new List<string>();
            List<string> entries = BuildEntries(repository, paths, null);

            foreach (GitExcludeVerifier.Verdict verdict in GitExcludeVerifier.Verify(repository, entries))
            {
                if (verdict.Status != GitExcludeVerifier.Status.Ignored) stillVisible.Add(verdict.Explanation);
            }

            return stillVisible;
        }

        /// <summary>
        /// The repository-relative exclude entries for the given paths, de-duplicated. When
        /// <paramref name="owners"/> is supplied it is filled with entry to owning-path, so a failing
        /// entry can be traced back to the path that has to be re-added.
        /// </summary>
        private static List<string> BuildEntries(GitRepositoryInfo repository, List<string> paths,
                                                 Dictionary<string, string> owners)
        {
            var entries = new List<string>();
            var seen = new HashSet<string>();

            foreach (string path in paths)
            {
                foreach (string entry in GitExcludeManager.GetExcludeEntries(repository, path))
                {
                    if (!seen.Add(entry)) continue;

                    entries.Add(entry);
                    owners?.Add(entry, path);
                }
            }

            return entries;
        }

        private static string Join(List<string> lines) => string.Join("\n- ", lines.ToArray());

        // ---- Fallback when git is unavailable -----------------------------------------------------

        private static void RunWithoutGit(GitRepositoryInfo repository, List<string> paths, bool verbose)
        {
            var missing = new List<string>();
            foreach (string path in paths)
            {
                if (!IsCoveredByExclude(repository, path)) missing.Add(path);
            }

            if (missing.Count == 0)
            {
                if (verbose)
                    Debug.Log($"<b>[Git Exclude]</b> All {paths.Count} tool path(s) are listed in the exclude " +
                              "file. Git was not available, so whether each entry takes effect is unverified.");
                return;
            }

            if (!verbose && !AutoRegisterEnabled) return;

            int added = GitExcludeManager.AddPathsToExclude(missing);
            if (added > 0)
            {
                Debug.Log($"<b>[Git Exclude]</b> Registered {added} tool path(s) in the local exclude file:\n" +
                          $"- {Join(missing)}\nGit was not available, so the entries could not be verified.");
            }
        }

        /// <summary>
        /// True when the exclude file lists <paramref name="projectPath"/> or any folder containing it.
        /// Only literal paths are understood - glob patterns are not evaluated and nothing outside the
        /// exclude file is consulted, so this is a last resort for when git itself cannot be asked.
        /// </summary>
        private static bool IsCoveredByExclude(GitRepositoryInfo repository, string projectPath)
        {
            string excludeFilePath = repository.ExcludeFilePath;
            if (!File.Exists(excludeFilePath)) return false;

            // Exclude patterns are repository-root-relative, so compare against the prefixed path.
            string normalized = repository.ToRepositoryPath(projectPath);

            foreach (string rawLine in File.ReadAllLines(excludeFilePath))
            {
                string line = rawLine.Trim().Replace('\\', '/');
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string entry = line.TrimEnd('/');
                if (entry.Length == 0) continue;

                if (normalized == entry || normalized.StartsWith(entry + "/", System.StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // ---- What to keep out of Git --------------------------------------------------------------

        /// <summary>This toolset's own folder, plus everything declared by a provider.</summary>
        private static List<string> CollectPaths()
        {
            var paths = new List<string>();

            string toolsetRoot = FindToolsetRoot();
            if (!string.IsNullOrEmpty(toolsetRoot)) paths.Add(toolsetRoot);

            foreach (string declared in GitExcludeProviders.CollectPaths())
            {
                if (!paths.Contains(declared)) paths.Add(declared);
            }

            return paths;
        }

        /// <summary>
        /// The folder containing this toolset, found by walking up from this script to its "Editor"
        /// folder and taking the parent. Walking rather than counting levels, so moving this file within
        /// the tree does not break the lookup.
        /// </summary>
        private static string FindToolsetRoot()
        {
            string scriptPath = FindOwnAssetPath();
            if (string.IsNullOrEmpty(scriptPath)) return null;

            for (string directory = ParentOf(scriptPath); directory != null; directory = ParentOf(directory))
            {
                if (Path.GetFileName(directory) != EditorFolderName) continue;

                string root = ParentOf(directory);

                // Refuse to return a project-wide folder. Dropped straight into Assets/Editor/, "the
                // folder above Editor" is Assets itself, and excluding that would hide the whole project.
                if (string.IsNullOrEmpty(root) || root == "Assets" || root == "Packages")
                {
                    Debug.LogWarning($"<b>[Git Exclude]</b> This toolset sits directly in " +
                                     $"'{root}/{EditorFolderName}', so it has no folder of its own to exclude. " +
                                     "Move it into a subfolder if you want it hidden from Git.");
                    return null;
                }

                return root;
            }

            return null;
        }

        private static string FindOwnAssetPath()
        {
            foreach (string guid in AssetDatabase.FindAssets($"{nameof(GitExcludeReminder)} t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == nameof(GitExcludeReminder))
                    return path;
            }

            return null;
        }

        private static string ParentOf(string path)
        {
            string parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? null : parent.Replace('\\', '/');
        }
    }
}
