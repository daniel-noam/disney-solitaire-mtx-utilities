using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Marks a method as a source of paths this toolset writes and that should stay out of Git.
    /// <para>
    /// The method must be static, take no parameters, and return <see cref="IEnumerable{T}"/> of
    /// project-root-relative paths - "ProjectSettings/MyTool.json", "SpriteEditorBackups". An attribute
    /// rather than an interface because the tools that own these files are static classes, which can
    /// neither implement an interface nor be instantiated.
    /// </para>
    /// </summary>
    /// <example><code>
    /// [GitExcludeProvider]
    /// private static IEnumerable&lt;string&gt; GitExcludePaths()
    /// {
    ///     yield return "ProjectSettings/FolderStructureSettings.json";
    /// }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class GitExcludeProviderAttribute : Attribute
    {
    }

    /// <summary>Collects every path declared with <see cref="GitExcludeProviderAttribute"/>.</summary>
    internal static class GitExcludeProviders
    {
        /// <summary>
        /// Declared paths, normalised and de-duplicated. Providers are found through
        /// <see cref="TypeCache"/>, so private methods are picked up and the scan costs nothing at
        /// domain reload.
        /// </summary>
        public static List<string> CollectPaths()
        {
            var paths = new List<string>();
            var seen = new HashSet<string>();

            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<GitExcludeProviderAttribute>())
            {
                foreach (string declared in Invoke(method))
                {
                    string normalized = Normalize(declared);
                    if (normalized.Length > 0 && seen.Add(normalized)) paths.Add(normalized);
                }
            }

            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        private static IEnumerable<string> Invoke(MethodInfo method)
        {
            string owner = method.DeclaringType != null ? method.DeclaringType.FullName : "<unknown>";

            if (!method.IsStatic || method.GetParameters().Length > 0 ||
                !typeof(IEnumerable<string>).IsAssignableFrom(method.ReturnType))
            {
                Debug.LogError($"<b>[Git Exclude]</b> '{owner}.{method.Name}' is marked " +
                               $"[{nameof(GitExcludeProviderAttribute)}] but is not a static, parameterless " +
                               "method returning IEnumerable<string>. Ignoring it.");
                return Array.Empty<string>();
            }

            try
            {
                var produced = method.Invoke(null, null) as IEnumerable<string>;
                if (produced == null) return Array.Empty<string>();

                // Materialised inside the try on purpose: providers are written as iterators, so their
                // body does not run until enumerated and an exception would otherwise escape past here.
                return new List<string>(produced);
            }
            catch (Exception e)
            {
                // One broken provider must not stop the rest of the toolset from registering.
                Debug.LogError($"<b>[Git Exclude]</b> '{owner}.{method.Name}' threw while listing paths: {e.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>Forward slashes, no leading "./", no trailing slash.</summary>
        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            string normalized = path.Replace('\\', '/').Trim();
            if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);

            return normalized.TrimEnd('/');
        }
    }
}
