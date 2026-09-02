using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace Tools.Editor.EditorUtilities.EasyUpload
{
    /// <summary>
    /// Finds where "Domino/Build/Build MTX Asset Bundles" put its output, without EasyUpload
    /// referencing the build pipeline at compile time.
    ///
    /// Reflection and EditorPrefs on purpose: LinkedAssets is symlinked into projects rather than
    /// being part of one, so an asmdef reference to a project-specific build assembly would stop the
    /// whole toolset compiling anywhere that assembly does not exist. The cost is that a rename over
    /// there turns this button off instead of failing the build, which is why nothing here throws and
    /// why <see cref="Explain"/> can say what was and was not found.
    /// </summary>
    public static class BuildOutputLocator
    {
        /// <summary>
        /// The key AssetBundleBuilderWindowMTX writes its Output Path to
        /// (DominoGeneric.BuildPipeline.Editor.Constants.BUNDLE_OUTPUT_PATH_PREF_KEY).
        /// </summary>
        private const string OutputPathPrefKey = "mtx_bundle_output_path";

        // Scanning every type in every loaded assembly is far too expensive to repeat per repaint,
        // and the answer cannot change without a domain reload — which resets these anyway.
        private static MethodInfo cachedAccessor;
        private static bool accessorSearched;
        private static bool? cachedToolPresent;

        /// <summary>
        /// The configured build root, or empty when the build tool is absent or was never pointed
        /// anywhere.
        ///
        /// Asks the build tool first: its accessor lets the BundleOutputPath environment variable
        /// override the stored value, which is how CI points the build elsewhere. Falls back to the
        /// same EditorPref the build window reads and writes.
        /// </summary>
        public static string Root()
        {
            var fromApi = FromBuildVariables();
            if (!string.IsNullOrEmpty(fromApi)) return fromApi;

            var stored = EditorPrefs.GetString(OutputPathPrefKey, "");
            return string.IsNullOrWhiteSpace(stored) ? "" : stored.Trim();
        }

        /// <summary>DominoGeneric.BuildPipeline.Editor.BuildVariables.GetBuildOutputPath(), if it is there.</summary>
        private static string FromBuildVariables()
        {
            try
            {
                if (!accessorSearched)
                {
                    accessorSearched = true;
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(SafeTypes)
                        .FirstOrDefault(t => t.Name == "BuildVariables" &&
                                             t.Namespace != null && t.Namespace.Contains("BuildPipeline"));

                    var method = type?.GetMethod("GetBuildOutputPath",
                        BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method != null && method.ReturnType == typeof(string)) cachedAccessor = method;
                }

                if (cachedAccessor == null) return "";
                return (cachedAccessor.Invoke(null, null) as string ?? "").Trim();
            }
            catch (Exception)
            {
                // A build tool mid-refactor must not take this window down.
                return "";
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch (Exception) { return Array.Empty<Type>(); }
        }

        /// <summary>True when a build tool that owns this path exists in the project at all.</summary>
        public static bool BuildToolPresent
        {
            get
            {
                if (cachedToolPresent.HasValue) return cachedToolPresent.Value;
                cachedToolPresent = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                    .Any(t => t.Name == "AssetBundleBuilderWindowMTX" || t.Name == "AssetBundleBuilder");
                return cachedToolPresent.Value;
            }
        }

        /// <summary>Why the button is off, for its tooltip.</summary>
        public static string Explain()
        {
            if (!BuildToolPresent) return "No MTX asset-bundle build tool in this project.";

            var root = Root();
            if (string.IsNullOrEmpty(root)) return "Build MTX Asset Bundles has no Output Path set yet.";
            if (!Directory.Exists(root)) return "That Output Path does not exist any more:\n" + root;

            return "";
        }
    }
}
