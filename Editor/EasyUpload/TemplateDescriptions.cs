using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>One template in the build that has a description JSON to write.</summary>
    public class TemplateDescription
    {
        /// <summary>
        /// The prefab's name, which is what the exporter names the file — not the bundle name.
        /// The two are usually equal but need not be: Unity lower-cases an asset bundle name and
        /// leaves the prefab's alone, and 145 prefabs in this project already differ that way.
        /// </summary>
        public string Name;

        public string AssetPath;

        /// <summary>The DynamicTemplate component the exporter is handed, held from the scan.</summary>
        public UnityEngine.Object Template;

        /// <summary>Bundles in the build carrying this prefab. More than one is unusual, not wrong.</summary>
        public List<string> Bundles = new List<string>();

        /// <summary>
        /// How many files in the build carry those bundle names — one per device the build covers.
        /// Kept so the card can show that the per-device copies were collapsed rather than missed.
        /// </summary>
        public int Copies;

        /// <summary>The chosen folder already holds a file of this name.</summary>
        public bool Present;

        /// <summary>Why the last write failed — usually a validation error. Empty once it succeeds.</summary>
        public string Error = "";

        /// <summary>
        /// Set when another template in the same build would write this same file. Both are held
        /// back rather than one silently overwriting the other.
        /// </summary>
        public string Conflict = "";

        public string FileName => Name + ".json";

        public bool CanWrite => string.IsNullOrEmpty(Conflict);
    }

    /// <summary>What the walk of the build folder found. Built off the main thread.</summary>
    public class BuildScan
    {
        /// <summary>File name → how many files in the build carry it, across every device folder.</summary>
        public Dictionary<string, int> FileNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every file walked, so the card can say what the build actually holds.</summary>
        public int Files;
    }

    /// <summary>What the build turned out to need. Built on the main thread, from a <see cref="BuildScan"/>.</summary>
    public class DescriptionScan
    {
        public List<TemplateDescription> Templates = new List<TemplateDescription>();

        /// <summary>Files walked under the build folder.</summary>
        public int Files;

        /// <summary>Distinct bundles of this project's found in the build, devices collapsed.</summary>
        public int Bundles;

        /// <summary>
        /// Distinct file names in the build that are not bundles this project builds.
        ///
        /// Worth keeping because "no template needs a JSON" and "I did not recognise any of this"
        /// look identical from the outside and mean completely different things. The second is
        /// what a build made from another branch looks like, and it has to be said out loud.
        /// </summary>
        public List<string> Unmatched = new List<string>();

        /// <summary>How many there were, which is not how many <see cref="Unmatched"/> kept.</summary>
        public int UnmatchedCount;
    }

    /// <summary>
    /// Writing the description JSON that a popup or a DTT needs alongside its bundle, for the
    /// bundles in a build that actually have one.
    ///
    /// Reflection for the same reason as <see cref="BuildOutputLocator"/>: this toolset is symlinked
    /// into projects rather than being part of one, and both the exporter (TemplateValidator, in the
    /// project's TemplateTools) and the component it takes (DynamicTemplate, in the templates
    /// submodule) live outside it. An asmdef reference to either would stop the whole toolset
    /// compiling anywhere they are absent, so instead the card turns itself off and
    /// <see cref="Explain"/> says which half is missing.
    /// </summary>
    public static class TemplateDescriptions
    {
        private const string ValidatorTypeName = "TemplateValidator";
        private const string ExportMethodName = "ExportDescription";
        private const string TemplateTypeName = "DynamicTemplate";

        /// <summary>Enough unmatched names to recognise the build by, not enough to hold thousands.</summary>
        private const int MaxUnmatchedKept = 12;

        // Scanning every type in every loaded assembly is far too expensive to repeat per repaint,
        // and the answer cannot change without a domain reload — which resets these anyway.
        private static bool searched;
        private static Type templateType;
        private static MethodInfo exportMethod;

        private static void Search()
        {
            if (searched) return;
            searched = true;

            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).ToList();

                // The base type, so a subclass counts too: a tooltip's DynamicTooltipTemplate
                // derives from it, and GetComponent on the base is what finds one.
                templateType = types.FirstOrDefault(t => t.Name == TemplateTypeName &&
                                                        typeof(Component).IsAssignableFrom(t));

                // Matched on shape rather than on the exact parameter types, because naming those
                // would mean resolving them here as well, and the first of the three is the very
                // type this class cannot reference.
                exportMethod = types
                    .Where(t => t.Name == ValidatorTypeName)
                    .Select(t => t.GetMethod(ExportMethodName, BindingFlags.Public | BindingFlags.Static))
                    .FirstOrDefault(m => m != null && m.GetParameters().Length == 3 &&
                                         m.GetParameters()[1].ParameterType == typeof(string) &&
                                         m.GetParameters()[2].ParameterType == typeof(bool));
            }
            catch (Exception)
            {
                // A tool mid-refactor must not take the window down.
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            catch (Exception) { return Array.Empty<Type>(); }
        }

        /// <summary>True when this project has both halves: the template component and the exporter.</summary>
        public static bool ToolPresent
        {
            get
            {
                Search();
                return templateType != null && exportMethod != null;
            }
        }

        /// <summary>Why the card is off, for its help box.</summary>
        public static string Explain()
        {
            Search();
            if (templateType == null && exportMethod == null)
                return "No template tooling in this project, so nothing here needs a config JSON.";
            if (templateType == null)
                return "No " + TemplateTypeName + " component in this project.";
            return "No " + ValidatorTypeName + "." + ExportMethodName + " in this project.";
        }

        /// <summary>
        /// Every file under the build folder and its sub-folders, counted by name.
        ///
        /// Names, not paths, because an MTX build is the same set of bundles written once per
        /// device — Android, iOS, StandaloneOSX, StandaloneWindows64 — and those are four copies of
        /// one bundle, not four bundles. Counting by name collapses them here, before anything
        /// downstream can turn them into four rows or four writes of the same file.
        ///
        /// Manifests are dropped because a bundle and its manifest share a stem, and matching the
        /// manifest would resolve the same template a second time.
        /// </summary>
        public static BuildScan Scan(string root, IList<string> dropPatterns)
        {
            var scan = new BuildScan();
            var rootFull = Path.GetFullPath(root);

            foreach (var path in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                scan.Files++;

                if (name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;
                if (UploadPlanner.IsDropped(name, dropPatterns)) continue;

                scan.FileNames.TryGetValue(name, out var seen);
                scan.FileNames[name] = seen + 1;
            }

            return scan;
        }

        /// <summary>
        /// Which of those names are bundles this project builds, and of those, which hold a template
        /// that gets a JSON. Main thread only — it reads the asset database.
        ///
        /// Asks the asset database for the bundle's contents rather than looking for a prefab of the
        /// same name: the bundle name is what the build wrote, and the assignment on the prefab's
        /// meta file is the only thing that says which prefab it came from. A name match would be a
        /// guess that happens to be right today.
        ///
        /// <paramref name="folders"/> is the whole of the badge/stamp/DR exclusion. A badge carries
        /// the same DynamicTemplate a popup does, so no component test can separate them; where the
        /// prefab lives is the only signal there is.
        /// </summary>
        public static DescriptionScan Resolve(BuildScan scan, IList<string> folders)
        {
            var result = new DescriptionScan();
            Search();
            if (scan == null) return result;

            result.Files = scan.Files;
            if (templateType == null) return result;

            var byPath = new Dictionary<string, TemplateDescription>(StringComparer.Ordinal);

            // Keyed case-insensitively but holding the registered spelling, because a lookup by
            // the build file's casing would come back empty: Unity lower-cases a bundle name when
            // it is assigned, and nothing forces the file on disk to agree.
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in AssetDatabase.GetAllAssetBundleNames()) known[name] = name;

            // Driven by what the build holds rather than by what the project could build, so the
            // names that matched nothing can be counted rather than silently skipped.
            foreach (var pair in scan.FileNames)
            {
                if (!known.TryGetValue(pair.Key, out var bundle))
                {
                    result.UnmatchedCount++;
                    if (result.Unmatched.Count < MaxUnmatchedKept) result.Unmatched.Add(pair.Key);
                    continue;
                }

                var copies = pair.Value;
                result.Bundles++;

                foreach (var assetPath in AssetDatabase.GetAssetPathsFromAssetBundle(bundle))
                {
                    if (!InFolders(assetPath, folders)) continue;

                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    var template = prefab != null ? prefab.GetComponent(templateType) : null;
                    if (template == null) continue;

                    // The same prefab reached through a second bundle is still one template, one
                    // file and one row — the extra bundle is worth recording, not repeating.
                    if (byPath.TryGetValue(assetPath, out var existing))
                    {
                        if (!existing.Bundles.Contains(bundle)) existing.Bundles.Add(bundle);
                        existing.Copies += copies;
                        continue;
                    }

                    var found = new TemplateDescription
                    {
                        Name = template.name,
                        AssetPath = assetPath,
                        Template = template,
                        Copies = copies,
                    };
                    found.Bundles.Add(bundle);
                    byPath.Add(assetPath, found);
                }
            }

            result.Unmatched.Sort(StringComparer.OrdinalIgnoreCase);
            result.Templates.AddRange(byPath.Values);
            result.Templates.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            FlagConflicts(result.Templates);
            return result;
        }

        /// <summary>
        /// Two different prefabs of the same name write the same file, and the second would land on
        /// top of the first with nothing said. This project already has such a pair
        /// (chef-kitchen-greece-tooltip-v1, in Templates and in DynamicTemplateTooltips), so both
        /// are held back and named instead.
        /// </summary>
        private static void FlagConflicts(List<TemplateDescription> templates)
        {
            var byFile = new Dictionary<string, TemplateDescription>(StringComparer.OrdinalIgnoreCase);

            foreach (var description in templates)
            {
                if (byFile.TryGetValue(description.FileName, out var first))
                {
                    var message = "Two templates in this build are both called " + description.Name +
                                  ", so they would write the same file:\n" +
                                  first.AssetPath + "\n" + description.AssetPath;
                    first.Conflict = message;
                    description.Conflict = message;
                    continue;
                }

                byFile.Add(description.FileName, description);
            }
        }

        /// <summary>
        /// Whether an asset sits under one of the configured folders. Prefix matching, so a folder
        /// covers everything nested under it, and boundary-aware so "Assets/Export/Templates" does
        /// not also claim "Assets/Export/TemplatesOld".
        /// </summary>
        public static bool InFolders(string assetPath, IList<string> folders)
        {
            if (folders == null || folders.Count == 0) return false;

            for (var i = 0; i < folders.Count; i++)
            {
                var folder = folders[i];
                if (string.IsNullOrEmpty(folder)) continue;
                if (assetPath.Length <= folder.Length) continue;
                if (assetPath[folder.Length] != '/') continue;
                if (assetPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Writes one description into the folder, replacing any file already there. Returns the
        /// error to show on the row, or empty when it was written.
        ///
        /// Never throws: one template failing validation must not stop the rest of the build's
        /// JSONs from being written.
        /// </summary>
        public static string Export(TemplateDescription description, string outputFolder)
        {
            Search();
            if (exportMethod == null) return "The exporter is not in this project.";
            if (description == null || description.Template == null)
                return "That template is not loaded any more; rescan the build.";

            try
            {
                // openFolder: false. The exporter reveals what it wrote, which for a build with
                // three templates would be three Finder windows; the card's drop zone opens the
                // folder on a double-click when that is actually wanted.
                exportMethod.Invoke(null, new object[] { description.Template, outputFolder, false });
                return "";
            }
            catch (TargetInvocationException e)
            {
                // The validation failure the exporter throws is the message worth showing; its
                // own wrapper is not.
                return (e.InnerException ?? e).Message;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }
}
