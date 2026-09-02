using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Runs git commands for the features that can't be done with plain file I/O (anything touching the
    /// index, such as skip-worktree). Synchronous: every caller is a direct response to a button press.
    /// </summary>
    internal static class GitCommandRunner
    {
        private const int TimeoutMilliseconds = 20000;
        private const int VersionProbeTimeoutMilliseconds = 5000;

        /// <summary>
        /// Tried in order. "git" alone covers a normal PATH; the absolute paths matter because Unity
        /// launched from Finder inherits a minimal PATH that often has no developer tooling on it.
        /// </summary>
        private static readonly string[] CandidateExecutables =
        {
            "git",
            "/usr/bin/git",
            "/usr/local/bin/git",
            "/opt/homebrew/bin/git",
        };

        private static string _resolvedExecutable;
        private static bool _resolutionAttempted;

        internal readonly struct Result
        {
            public bool Success { get; }
            public string Output { get; }
            public string Error { get; }

            public Result(bool success, string output, string error)
            {
                Success = success;
                Output = output ?? "";
                Error = error ?? "";
            }

            /// <summary>Whichever stream explains a failure, for logging.</summary>
            public string FailureMessage => Error.Length > 0 ? Error.Trim() : Output.Trim();
        }

        public static bool IsGitAvailable => ResolveExecutable() != null;

        public static Result Run(string repositoryRoot, params string[] arguments)
        {
            string executable = ResolveExecutable();
            if (executable == null)
            {
                return new Result(false, "", "git executable not found. Install the command line tools or " +
                                            "add git to PATH.");
            }

            return Execute(executable, repositoryRoot, BuildArguments(repositoryRoot, arguments), TimeoutMilliseconds);
        }

        private static string ResolveExecutable()
        {
            if (_resolutionAttempted) return _resolvedExecutable;
            _resolutionAttempted = true;

            foreach (string candidate in CandidateExecutables)
            {
                // Absolute candidates are cheap to rule out without spawning anything.
                if (candidate.IndexOf('/') >= 0 && !File.Exists(candidate)) continue;

                Result probe = Execute(candidate, null, "--version", VersionProbeTimeoutMilliseconds);
                if (!probe.Success) continue;

                _resolvedExecutable = candidate;
                return candidate;
            }

            return null;
        }

        private static Result Execute(string executable, string workingDirectory, string arguments, int timeoutMilliseconds)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                startInfo.WorkingDirectory = workingDirectory;

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return new Result(false, "", $"Could not start '{executable}'.");

                    // stderr is read asynchronously so a process that fills one pipe while we block on the
                    // other can't deadlock us.
                    var errorTask = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = errorTask.Result;

                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        TryKill(process);
                        return new Result(false, output, $"'{executable} {arguments}' timed out.");
                    }

                    return new Result(process.ExitCode == 0, output, error);
                }
            }
            catch (Exception e)
            {
                return new Result(false, "", e.Message);
            }
        }

        private static void TryKill(Process process)
        {
            try { process.Kill(); }
            catch (Exception e) { Debug.LogWarning($"<b>[Git]</b> Could not kill timed-out git process: {e.Message}"); }
        }

        private static string BuildArguments(string repositoryRoot, string[] arguments)
        {
            var builder = new StringBuilder();

            // -C rather than relying on the working directory alone, so the repository is unambiguous
            // even for a project nested inside it.
            if (!string.IsNullOrEmpty(repositoryRoot))
            {
                builder.Append("-C ").Append(Quote(repositoryRoot));
            }

            foreach (string argument in arguments)
            {
                if (string.IsNullOrEmpty(argument)) continue;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(Quote(argument));
            }

            return builder.ToString();
        }

        /// <summary>Quotes an argument unless it is a bare option or already safe.</summary>
        private static string Quote(string argument)
        {
            if (argument == "--" || argument.StartsWith("-")) return argument;
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }
    }
}
