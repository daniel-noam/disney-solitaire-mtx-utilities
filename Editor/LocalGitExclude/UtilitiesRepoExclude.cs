using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Keeps this repository out of the host project's git.
    ///
    /// The tools live in their own repository, cloned into the project rather than added as a
    /// submodule, so that the project's git never carries a pointer to them: nothing to commit when
    /// they change, and nothing to delete by accident. The host repository should not see the
    /// folder at all, which is what this registers.
    ///
    /// Registered rather than written by hand so it survives being cloned into a new project, and
    /// so nobody has to know to do it.
    ///
    /// One thing this cannot do on its own: a Unity .gitignore normally ends with
    /// `![Aa]ssets/**/*.meta` to make sure meta files are never lost, and a rule in .gitignore
    /// beats anything in .git/info/exclude. So the folder disappears but its .meta keeps showing up
    /// as untracked. Silencing that one needs a line in the project's own .gitignore, after the
    /// negation — see README.md.
    /// </summary>
    internal static class UtilitiesRepoExclude
    {
        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            var path = ProjectRelativeRoot();
            if (string.IsNullOrEmpty(path)) yield break;

            yield return path;
            yield return path + ".meta";
        }

        /// <summary>
        /// Where this repository sits inside the project, worked out from where this file is.
        ///
        /// The compiler knows the path of the file it is compiling, which is exact and costs
        /// nothing — better than searching the AssetDatabase for a folder by name, and it keeps
        /// working if the repository is cloned somewhere other than Assets/Shared.
        /// </summary>
        private static string ProjectRelativeRoot()
        {
            var file = SourcePath();
            if (string.IsNullOrEmpty(file)) return null;

            // <root>/Editor/LocalGitExclude/ThisFile.cs
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file)));
            if (string.IsNullOrEmpty(root)) return null;

            // Application.dataPath is <project>/Assets, so its parent is what git paths start from.
            var project = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(project)) return null;

            root = root.Replace('\\', '/');
            project = project.Replace('\\', '/').TrimEnd('/') + "/";

            return root.StartsWith(project) ? root.Substring(project.Length) : null;
        }

        private static string SourcePath([CallerFilePath] string path = null) => path;
    }
}
