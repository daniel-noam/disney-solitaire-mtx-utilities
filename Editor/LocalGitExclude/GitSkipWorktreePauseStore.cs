using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Remembers which files had their skip-worktree bit cleared *temporarily*, so a paused file can be
    /// told apart from one that was never skipped and can be restored later.
    /// <para>
    /// Git itself has no concept of a paused skip-worktree - the bit is either set or not - so the list
    /// is kept alongside the repository, inside the Git directory. That location is local by
    /// construction: it is never committed and never shared.
    /// </para>
    /// </summary>
    internal static class GitSkipWorktreePauseStore
    {
        private const string FolderName = "unity-local-overrides";
        private const string FileName = "paused-skip-worktree.tsv";

        private const string Header =
            "# Files whose git skip-worktree bit is temporarily cleared, written by the Unity " +
            "Git Local Overrides tool.\n" +
            "# Format: <content hash at pause time><TAB><repository-relative path>\n";

        internal readonly struct PausedEntry
        {
            /// <summary>Repository-relative path, matching what git reports.</summary>
            public string Path { get; }

            /// <summary>
            /// Hash of the working-tree file when it was paused, or empty when the file was missing.
            /// Used only to tell the user whether the file changed while unprotected.
            /// </summary>
            public string ContentHash { get; }

            public PausedEntry(string path, string contentHash)
            {
                Path = path;
                ContentHash = contentHash ?? "";
            }
        }

        public static List<PausedEntry> Load(GitRepositoryInfo repository)
        {
            var entries = new List<PausedEntry>();
            if (!repository.IsValid) return entries;

            string path = GetStorePath(repository);
            if (!File.Exists(path)) return entries;

            try
            {
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int separator = line.IndexOf('\t');
                    if (separator < 0) continue;

                    string storedPath = line.Substring(separator + 1);
                    if (storedPath.Length == 0) continue;

                    entries.Add(new PausedEntry(storedPath, line.Substring(0, separator)));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> Could not read paused skip-worktree list: {e.Message}");
            }

            return entries;
        }

        public static void Save(GitRepositoryInfo repository, IEnumerable<PausedEntry> entries)
        {
            if (!repository.IsValid) return;

            string path = GetStorePath(repository);

            try
            {
                var builder = new StringBuilder(Header);
                foreach (PausedEntry entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Path) || entry.Path.IndexOf('\t') >= 0) continue;
                    builder.Append(entry.ContentHash).Append('\t').Append(entry.Path).Append('\n');
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, builder.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError($"<b>[Git Exclude]</b> Could not write paused skip-worktree list to '{path}': {e.Message}");
            }
        }

        /// <summary>Hash of a working-tree file, or empty when it cannot be read.</summary>
        public static string ComputeContentHash(GitRepositoryInfo repository, string repositoryPath)
        {
            try
            {
                string absolute = Path.Combine(repository.RepositoryRoot, repositoryPath);
                if (!File.Exists(absolute)) return "";

                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(File.ReadAllBytes(absolute));
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> Could not hash '{repositoryPath}': {e.Message}");
                return "";
            }
        }

        private static string GetStorePath(GitRepositoryInfo repository) =>
            Path.Combine(repository.GitDirectory, FolderName, FileName);
    }
}
