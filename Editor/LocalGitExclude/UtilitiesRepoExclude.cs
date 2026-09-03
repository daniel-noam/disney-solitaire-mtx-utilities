using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Keeps this repository out of the host project's git, without asking anybody to set it up.
    ///
    /// These tools live in their own repository, cloned into a project rather than added as a
    /// submodule. The project's git should carry no trace of them: nothing to commit when they
    /// change, nothing to stage or discard, and nothing for somebody who does not want the tools
    /// to inherit.
    ///
    /// Two files are in the way of that, and one of them cannot be handled by
    /// .git/info/exclude alone:
    ///
    ///   - the folder itself.
    ///   - the folder's .meta, which sits beside it rather than inside it. A Unity .gitignore ends
    ///     with `![Aa]ssets/**/*.meta` so that meta files are never lost by accident, and a rule in
    ///     .gitignore beats anything in .git/info/exclude — so the folder disappears and its meta
    ///     does not.
    ///
    /// The way round it is that git ranks ignore files by depth: a .gitignore closer to the file
    /// wins over one further up. So a .gitignore beside this repository overrides the negation at
    /// the project root. That file is itself excluded through info/exclude, where no negation
    /// applies, and the whole arrangement is invisible.
    ///
    /// Everything written here is local to the machine: an untracked .gitignore and lines in
    /// .git/info/exclude. Nothing lands in a commit.
    /// </summary>
    [InitializeOnLoad]
    internal static class UtilitiesRepoExclude
    {
        static UtilitiesRepoExclude()
        {
            // Deferred: this touches the file system, and a domain reload is not the moment.
            EditorApplication.delayCall += EnsureSiblingIgnore;
        }

        /// <summary>
        /// The exclude manager takes the .gitignore. The two paths it covers are handled by the
        /// file itself, which is the only way to beat the meta negation.
        /// </summary>
        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            var ignore = ProjectRelative(SiblingIgnorePath());
            if (!string.IsNullOrEmpty(ignore)) yield return ignore;
        }

        private static void EnsureSiblingIgnore()
        {
            var path = SiblingIgnorePath();
            var folder = FolderName();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return;

            var wanted = new[] { folder + "/", folder + ".meta" };

            var lines = new List<string>();
            if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));

            var added = false;
            foreach (var line in wanted)
            {
                if (lines.Contains(line)) continue;
                lines.Add(line);
                added = true;
            }

            // Only ever appends, and only when something is missing: the file may belong to
            // somebody else, and rewriting it every reload would be its own kind of noise.
            if (!added) return;

            try
            {
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
            }
            catch (IOException exception)
            {
                Debug.LogWarning("[Utilities] Could not write " + path + ": " + exception.Message);
            }
        }

        /// <summary>The .gitignore that sits beside this repository, in its parent folder.</summary>
        private static string SiblingIgnorePath()
        {
            var root = RepositoryRoot();
            return string.IsNullOrEmpty(root)
                ? null
                : Path.Combine(Path.GetDirectoryName(root) ?? string.Empty, ".gitignore").Replace('\\', '/');
        }

        private static string FolderName()
        {
            var root = RepositoryRoot();
            return string.IsNullOrEmpty(root) ? null : Path.GetFileName(root);
        }

        /// <summary>
        /// Where this repository sits, worked out from where this file is. The compiler knows the
        /// path of the file it is compiling, which is exact and costs nothing — and it keeps
        /// working if somebody clones the tools somewhere other than Assets/Shared.
        /// </summary>
        private static string RepositoryRoot()
        {
            var file = SourcePath();
            if (string.IsNullOrEmpty(file)) return null;

            // <root>/Editor/LocalGitExclude/ThisFile.cs
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)));
            return string.IsNullOrEmpty(root) ? null : root.Replace('\\', '/');
        }

        /// <summary>An absolute path as git would name it, or null when it is outside the project.</summary>
        private static string ProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;

            // Application.dataPath is <project>/Assets, so its parent is what git paths start from.
            var project = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(project)) return null;

            project = project.Replace('\\', '/').TrimEnd('/') + "/";
            absolute = absolute.Replace('\\', '/');

            return absolute.StartsWith(project) ? absolute.Substring(project.Length) : null;
        }

        private static string SourcePath([CallerFilePath] string path = null) => path;
    }
}
