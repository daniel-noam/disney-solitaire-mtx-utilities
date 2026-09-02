using System.Collections.Generic;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Asks Git whether an exclude entry actually takes effect, rather than assuming that writing a line
    /// into .git/info/exclude is enough. Two ways a written line silently does nothing:
    /// <list type="bullet">
    /// <item>A higher-precedence pattern wins. .gitignore outranks .git/info/exclude, so Unity's standard
    /// "![Aa]ssets/**/*.meta" re-include makes every .meta entry under Assets/ dead on arrival.</item>
    /// <item>The path is already tracked. Ignore rules never apply to tracked files - skip-worktree is
    /// the mechanism for those.</item>
    /// </list>
    /// </summary>
    internal static class GitExcludeVerifier
    {
        /// <summary>Matches <see cref="GitSkipWorktree"/>: keeps one command well under the OS argument limit.</summary>
        private const int PathsPerBatch = 200;

        internal enum Status
        {
            /// <summary>A pattern matched and nothing overrode it - Git will leave the path alone.</summary>
            Ignored,

            /// <summary>In the index, so no ignore rule can hide it however it is written.</summary>
            Tracked,

            /// <summary>Nothing matched, or a negation re-included it.</summary>
            NotIgnored,
        }

        internal readonly struct Verdict
        {
            public string RepositoryPath { get; }
            public Status Status { get; }

            /// <summary>"file:line" of the pattern that decided this, empty when nothing matched.</summary>
            public string DecidedBy { get; }

            /// <summary>The deciding pattern, empty when nothing matched. Leading '!' means a negation won.</summary>
            public string Pattern { get; }

            public Verdict(string repositoryPath, Status status, string decidedBy, string pattern)
            {
                RepositoryPath = repositoryPath;
                Status = status;
                DecidedBy = decidedBy ?? "";
                Pattern = pattern ?? "";
            }

            /// <summary>One line explaining a non-ignored verdict, for the console.</summary>
            public string Explanation
            {
                get
                {
                    switch (Status)
                    {
                        case Status.Tracked:
                            return $"{RepositoryPath} - tracked by Git, so exclude patterns do not apply";
                        case Status.NotIgnored when Pattern.StartsWith("!"):
                            return $"{RepositoryPath} - re-included by '{Pattern}' in {DecidedBy}, which outranks " +
                                   "the exclude file";
                        case Status.NotIgnored:
                            return $"{RepositoryPath} - no pattern matched it";
                        default:
                            return RepositoryPath;
                    }
                }
            }
        }

        /// <summary>
        /// Resolves the real status of each repository-relative path. Returns an empty list when Git is
        /// unavailable, so callers fall back to "assume it worked" rather than reporting false alarms.
        /// </summary>
        public static List<Verdict> Verify(GitRepositoryInfo repository, IList<string> repositoryPaths)
        {
            var verdicts = new List<Verdict>();
            if (!repository.IsValid || repositoryPaths == null || repositoryPaths.Count == 0) return verdicts;
            if (!GitCommandRunner.IsGitAvailable) return verdicts;

            HashSet<string> tracked = ListTracked(repository, repositoryPaths);

            for (int start = 0; start < repositoryPaths.Count; start += PathsPerBatch)
            {
                // -n reports non-matching paths too, so every input gets a verdict rather than only the
                // ignored ones. --no-index keeps this a pure question about patterns; whether the path is
                // tracked is answered separately so the two failures can be told apart.
                var arguments = new List<string> { "check-ignore", "-v", "-n", "--no-index", "--" };

                int end = Mathf.Min(start + PathsPerBatch, repositoryPaths.Count);
                for (int i = start; i < end; i++) arguments.Add(repositoryPaths[i]);

                GitCommandRunner.Result result = GitCommandRunner.Run(repository.RepositoryRoot, arguments.ToArray());

                // Exit code 1 just means "nothing matched", which -v -n still reports on stdout.
                if (!result.Success && result.Output.Length == 0)
                {
                    Debug.LogWarning($"<b>[Git Exclude]</b> Could not verify exclude entries: {result.FailureMessage}");
                    continue;
                }

                foreach (string rawLine in result.Output.Split('\n'))
                {
                    if (TryParse(rawLine.TrimEnd('\r'), tracked, out Verdict verdict)) verdicts.Add(verdict);
                }
            }

            return verdicts;
        }

        /// <summary>
        /// Parses one "&lt;source&gt;:&lt;line&gt;:&lt;pattern&gt;TAB&lt;path&gt;" record. A path nothing
        /// matched comes back as the literal "::" in the source field.
        /// </summary>
        private static bool TryParse(string line, ICollection<string> tracked, out Verdict verdict)
        {
            verdict = default;

            int tab = line.IndexOf('\t');
            if (tab < 0) return false;

            string source = line.Substring(0, tab);
            string path = line.Substring(tab + 1);
            if (path.Length == 0) return false;

            SplitSource(source, out string file, out string lineNumber, out string pattern);

            // Tracked beats everything: the pattern may well match, but Git still keeps reporting the file.
            if (IsTracked(tracked, path))
            {
                verdict = new Verdict(path, Status.Tracked, Combine(file, lineNumber), pattern);
                return true;
            }

            bool ignored = pattern.Length > 0 && !pattern.StartsWith("!");
            verdict = new Verdict(path, ignored ? Status.Ignored : Status.NotIgnored,
                                  Combine(file, lineNumber), pattern);
            return true;
        }

        /// <summary>Splits from the left: the source is a path and the pattern may itself contain colons.</summary>
        private static void SplitSource(string source, out string file, out string lineNumber, out string pattern)
        {
            file = "";
            lineNumber = "";
            pattern = "";

            int firstColon = source.IndexOf(':');
            if (firstColon < 0) return;

            int secondColon = source.IndexOf(':', firstColon + 1);
            if (secondColon < 0) return;

            file = source.Substring(0, firstColon);
            lineNumber = source.Substring(firstColon + 1, secondColon - firstColon - 1);
            pattern = source.Substring(secondColon + 1);
        }

        private static string Combine(string file, string lineNumber)
        {
            if (file.Length == 0) return "";
            return lineNumber.Length == 0 ? file : file + ":" + lineNumber;
        }

        /// <summary>
        /// True when the path itself is tracked, or when anything under it is. A folder pathspec makes
        /// "git ls-files" report the tracked files inside rather than the folder, so an exact match alone
        /// would call a folder full of committed files untracked.
        /// </summary>
        private static bool IsTracked(ICollection<string> tracked, string path)
        {
            if (tracked.Contains(path)) return true;

            string prefix = path + "/";
            foreach (string entry in tracked)
            {
                if (entry.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Tracked files at or under the given paths, as Git reports them.</summary>
        private static HashSet<string> ListTracked(GitRepositoryInfo repository, IList<string> repositoryPaths)
        {
            var tracked = new HashSet<string>();

            for (int start = 0; start < repositoryPaths.Count; start += PathsPerBatch)
            {
                var arguments = new List<string> { "ls-files", "--" };

                int end = Mathf.Min(start + PathsPerBatch, repositoryPaths.Count);
                for (int i = start; i < end; i++) arguments.Add(repositoryPaths[i]);

                GitCommandRunner.Result result = GitCommandRunner.Run(repository.RepositoryRoot, arguments.ToArray());
                if (!result.Success) continue;

                foreach (string rawLine in result.Output.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Length > 0) tracked.Add(line);
                }
            }

            return tracked;
        }
    }
}
