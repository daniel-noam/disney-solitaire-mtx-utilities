using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Resolves profile path templates against a variable value and creates the folders on disk.
    /// Paths under the Assets folder trigger a single AssetDatabase.Refresh() so Unity imports them
    /// and generates .meta files; paths elsewhere are created with plain System.IO.
    /// </summary>
    public static class FolderStructureEngine
    {
        /// <summary>A single resolved folder: its absolute path plus a friendly display path.</summary>
        public struct ResolvedPath
        {
            public string absolutePath;
            public string displayPath;
            public bool underAssets;
            public bool addToQuickNav;
        }

        public struct CreateResult
        {
            public int created;
            public int skipped;
            public int quickNavAdded;
            public bool quickNavRequested;
            public bool quickNavAvailable;
            public List<string> errors;
        }

        /// <summary>The Assets folder, e.g. "&lt;project&gt;/Assets".</summary>
        public static string AssetsRoot => Application.dataPath.Replace('\\', '/');

        /// <summary>The folder containing Assets, e.g. "&lt;project&gt;".</summary>
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');

        /// <summary>Removes characters that are illegal in file/folder names from a variable value.</summary>
        public static string SanitizeValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = new string(value.Where(c => !invalid.Contains(c)).ToArray()).Trim();

            // "." and ".." survive the invalid-character filter but would retarget the whole template at a
            // parent directory, creating folders outside the intended tree. A value that is only dots is
            // rejected; dots inside a name (e.g. "v1.2") are fine.
            return cleaned.Trim('.').Length == 0 ? "" : cleaned;
        }

        private static string GetBasePath(FolderEntry entry)
        {
            switch (entry.root)
            {
                case PathRoot.Assets: return AssetsRoot;
                case PathRoot.ProjectRoot: return ProjectRoot;
                case PathRoot.Custom: return string.IsNullOrWhiteSpace(entry.customRoot) ? ProjectRoot : entry.customRoot.Replace('\\', '/').TrimEnd('/');
                default: return AssetsRoot;
            }
        }

        private static string RootLabel(FolderEntry entry)
        {
            switch (entry.root)
            {
                case PathRoot.Assets: return "Assets";
                case PathRoot.ProjectRoot: return "<project>";
                case PathRoot.Custom: return string.IsNullOrWhiteSpace(entry.customRoot) ? "<project>" : entry.customRoot.TrimEnd('/', '\\');
                default: return "Assets";
            }
        }

        /// <summary>
        /// Resolves every entry of a profile against the given variable value.
        /// Returns the list of absolute + display paths (illegal/empty entries are skipped).
        /// </summary>
        public static List<ResolvedPath> Resolve(FolderProfile profile, string variableValue)
        {
            var results = new List<ResolvedPath>();
            if (profile == null) return results;

            string safeValue = SanitizeValue(variableValue);

            foreach (FolderEntry entry in profile.folders)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.pathTemplate)) continue;

                string relative = entry.pathTemplate.Replace(profile.Token, safeValue).Replace('\\', '/').Trim().Trim('/');
                if (string.IsNullOrEmpty(relative)) continue;

                string basePath = GetBasePath(entry);
                string absolute = (basePath.TrimEnd('/') + "/" + relative).Replace('\\', '/');

                bool underAssets = absolute == AssetsRoot || absolute.StartsWith(AssetsRoot + "/", StringComparison.Ordinal);

                results.Add(new ResolvedPath
                {
                    absolutePath = absolute,
                    displayPath = RootLabel(entry) + "/" + relative,
                    underAssets = underAssets,
                    addToQuickNav = entry.addToQuickNav
                });
            }

            return results;
        }

        /// <summary>
        /// Resolves the QuickNav tab name for a profile: substitutes the variable, falls back to the
        /// profile name, then PascalCases the result so "chef-challenge" becomes "ChefChallenge".
        /// </summary>
        public static string ResolveTabName(FolderProfile profile, string variableValue)
        {
            if (profile == null) return "";
            string safeValue = SanitizeValue(variableValue);
            string name = (profile.quickNavTabName ?? "").Replace(profile.Token, safeValue).Trim();
            if (string.IsNullOrEmpty(name)) name = profile.profileName;
            return ToPascalCase(name);
        }

        /// <summary>Splits on '-', '_' and whitespace, capitalizing each segment (e.g. "chef-challenge" -> "ChefChallenge").</summary>
        public static string ToPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string[] parts = input.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (string part in parts)
                sb.Append(char.ToUpper(part[0])).Append(part.Substring(1));

            return sb.Length > 0 ? sb.ToString() : input;
        }

        /// <summary>Creates all resolved folders (skipping existing ones) and optionally registers flagged folders in QuickNav.</summary>
        public static CreateResult Create(FolderProfile profile, string variableValue)
        {
            var result = new CreateResult { errors = new List<string>() };
            List<ResolvedPath> paths = Resolve(profile, variableValue);

            bool touchedAssets = false;
            var favorites = new List<string>();

            foreach (ResolvedPath p in paths)
            {
                try
                {
                    bool exists = Directory.Exists(p.absolutePath);
                    if (exists)
                    {
                        result.skipped++;
                    }
                    else
                    {
                        Directory.CreateDirectory(p.absolutePath);
                        result.created++;
                        if (p.underAssets) touchedAssets = true;
                    }

                    // Favorite the folder whether it was newly created or already present.
                    if (p.addToQuickNav)
                        favorites.Add(p.absolutePath);
                }
                catch (Exception e)
                {
                    result.errors.Add($"{p.displayPath}: {e.Message}");
                }
            }

            if (touchedAssets)
                AssetDatabase.Refresh();

            result.quickNavRequested = favorites.Count > 0;
            if (result.quickNavRequested)
            {
                result.quickNavAvailable = QuickNavBridge.IsAvailable;
                if (result.quickNavAvailable)
                {
                    string tabName = ResolveTabName(profile, variableValue);
                    result.quickNavAdded = QuickNavBridge.AddFoldersToTab(tabName, profile.quickNavTabColor, favorites);
                }
            }

            return result;
        }
    }
}
