using System;
using System.IO;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Locates the Git repository containing the Unity project, which is not necessarily the project
    /// folder itself - a project often sits in a subfolder of a larger repo.
    /// <para>
    /// This matters for the exclude file because its patterns are relative to the repository root,
    /// while Unity asset paths are relative to the project folder. <see cref="ToRepositoryPath"/>
    /// bridges the two.
    /// </para>
    /// </summary>
    public readonly struct GitRepositoryInfo
    {
        private const string GitDirName = ".git";
        private const string GitDirPointerPrefix = "gitdir:";

        /// <summary>Folder holding the .git entry, e.g. "/work/my-repo".</summary>
        public string RepositoryRoot { get; }

        /// <summary>
        /// The real Git directory. Usually "&lt;root&gt;/.git", but for worktrees and submodules the
        /// .git entry is a file pointing somewhere else and this is that target.
        /// </summary>
        public string GitDirectory { get; }

        /// <summary>
        /// Repository-root-relative path of the Unity project folder: "" when the project *is* the
        /// repository root, otherwise something like "client/UnityProject".
        /// </summary>
        public string ProjectPrefix { get; }

        public bool IsValid => !string.IsNullOrEmpty(GitDirectory);

        public string ExcludeFilePath => Path.Combine(GitDirectory, "info", "exclude");

        /// <summary>True when the Unity project folder is not itself the repository root.</summary>
        public bool IsNested => ProjectPrefix.Length > 0;

        private GitRepositoryInfo(string repositoryRoot, string gitDirectory, string projectPrefix)
        {
            RepositoryRoot = repositoryRoot;
            GitDirectory = gitDirectory;
            ProjectPrefix = projectPrefix;
        }

        /// <summary>Rewrites a Unity asset path into a repository-root-relative exclude pattern.</summary>
        public string ToRepositoryPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;

            string normalized = assetPath.Replace('\\', '/');
            return ProjectPrefix.Length == 0 ? normalized : ProjectPrefix + "/" + normalized;
        }

        /// <summary>
        /// Walks up from the Unity project folder looking for a .git entry. Returns an invalid
        /// instance when the project is not inside a repository at all.
        /// </summary>
        public static GitRepositoryInfo Locate()
        {
            string projectRoot = NormalizePath(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectRoot)) return default;

            for (DirectoryInfo dir = new DirectoryInfo(projectRoot); dir != null; dir = dir.Parent)
            {
                string gitDirectory = ResolveGitDirectory(Path.Combine(dir.FullName, GitDirName));
                if (gitDirectory == null) continue;

                string root = NormalizePath(dir.FullName);
                return new GitRepositoryInfo(root, gitDirectory, BuildProjectPrefix(root, projectRoot));
            }

            return default;
        }

        /// <summary>Resolves a .git entry to a real directory, following worktree/submodule pointer files.</summary>
        private static string ResolveGitDirectory(string gitEntryPath)
        {
            if (Directory.Exists(gitEntryPath)) return NormalizePath(gitEntryPath);
            if (!File.Exists(gitEntryPath)) return null;

            // Worktrees and submodules write a one-line "gitdir: <path>" file in place of the directory.
            try
            {
                foreach (string line in File.ReadAllLines(gitEntryPath))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith(GitDirPointerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    string target = trimmed.Substring(GitDirPointerPrefix.Length).Trim();
                    if (target.Length == 0) return null;

                    if (!Path.IsPathRooted(target))
                        target = Path.Combine(Path.GetDirectoryName(gitEntryPath), target);

                    return Directory.Exists(target) ? NormalizePath(Path.GetFullPath(target)) : null;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> Could not read '{gitEntryPath}': {e.Message}");
            }

            return null;
        }

        private static string BuildProjectPrefix(string repositoryRoot, string projectRoot)
        {
            // projectRoot is always at or below repositoryRoot, because the search walked up from it.
            if (string.Equals(repositoryRoot, projectRoot, StringComparison.Ordinal)) return "";

            return projectRoot.Substring(repositoryRoot.Length).Trim('/');
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string normalized = path.Replace('\\', '/');
            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }
    }
}
