using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Where a folder entry's <see cref="FolderEntry.pathTemplate"/> is rooted.
    /// Assets      = the project's Assets folder (Application.dataPath).
    /// ProjectRoot = the folder that contains Assets (one level up), e.g. for "art/..." siblings.
    /// Custom      = an explicit absolute base path typed on the entry.
    /// </summary>
    public enum PathRoot { Assets, ProjectRoot, Custom }

    /// <summary>A single folder to create, relative to a chosen root.</summary>
    [Serializable]
    public class FolderEntry
    {
        public PathRoot root = PathRoot.Assets;

        // Only used when root == PathRoot.Custom.
        public string customRoot = "";

        // Relative path; may contain the profile's variable token, e.g. "mtx/{event}/Popup".
        // Intermediate folders are created automatically.
        public string pathTemplate = "";

        // When true, this folder is added to the QuickNav favorites tab after creation
        // (only takes effect if the QuickNavigation tool is present in the project).
        public bool addToQuickNav = false;
    }

    /// <summary>
    /// A named set of folders. The profile declares a single variable (e.g. "event"); its token
    /// is the variable name wrapped in braces (e.g. "{event}") and is substituted into every
    /// entry's path template when creating.
    /// </summary>
    [Serializable]
    public class FolderProfile
    {
        public string profileName = "New Profile";

        // The single variable this profile prompts for. Token used in templates is "{" + variableName + "}".
        public string variableName = "name";

        public List<FolderEntry> folders = new List<FolderEntry>();

        // Name of the QuickNav tab that flagged folders are added to. Supports the variable token,
        // e.g. "{event}" creates one tab per event. Falls back to the profile name when blank.
        public string quickNavTabName = "{name}";

        // Tint applied to a newly created QuickNav tab.
        public Color quickNavTabColor = new Color(0.35f, 0.7f, 1f);

        [HideInInspector] public bool isExpanded = true;

        public string Token => "{" + variableName + "}";
    }

    /// <summary>
    /// Persisted collection of folder-structure profiles. Stored as JSON under ProjectSettings/
    /// (kept out of the asset DB but still shared via VCS), matching QuickSnapSettings.
    /// </summary>
    public class FolderStructureSettings : ScriptableObject
    {
        public List<FolderProfile> profiles = new List<FolderProfile>();

        /// <summary>Project-root-relative, so the Git exclude provider and the loader cannot drift apart.</summary>
        private const string ProjectRelativeSettingsPath = "ProjectSettings/FolderStructureSettings.json";

        // Derived from dataPath rather than Directory.GetCurrentDirectory(): Unity's working directory
        // is normally the project root but is not guaranteed to stay there, and if it moves the settings
        // silently save to the wrong place.
        private static string SettingsPath =>
            Path.Combine(FolderStructureEngine.ProjectRoot, ProjectRelativeSettingsPath);

        private const string LegacyAssetPath = "Assets/FolderStructureSettings.asset";

        /// <summary>
        /// ProjectSettings/ is normally committed, so this file would otherwise ride along into the shared
        /// repository. The legacy asset is not listed: migration deletes it.
        /// </summary>
        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            yield return ProjectRelativeSettingsPath;
        }

        private static FolderStructureSettings _instance;
        public static FolderStructureSettings Instance
        {
            get
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath, EditorJsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                // Called from OnGUI and OnDisable, where an exception would surface as a broken window.
                Debug.LogError($"[FolderStructureSettings] Failed to save settings to '{SettingsPath}': {e.Message}");
            }
        }

        private static FolderStructureSettings Load()
        {
            // In-memory only: not an asset, so keep it out of the project and off disk-save passes.
            var settings = CreateInstance<FolderStructureSettings>();
            settings.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(SettingsPath), settings);
                }
                else
                {
                    // Fresh install, or an upgrade from the old ScriptableObject asset.
                    TryMigrateLegacyAsset(settings);
                    SeedDefaultsIfEmpty(settings);
                    settings.Save();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FolderStructureSettings] Failed to load settings, resetting to defaults: {e.Message}");
                SeedDefaultsIfEmpty(settings);
            }

            return settings;
        }

        private static void SeedDefaultsIfEmpty(FolderStructureSettings settings)
        {
            if (settings.profiles.Count == 0)
                settings.profiles.Add(BuildMtxTemplateProfile());
        }

        /// <summary>
        /// One-time upgrade from the old ScriptableObject asset in Assets/ into ProjectSettings JSON.
        /// Deletes the legacy asset when migration succeeds.
        /// </summary>
        private static bool TryMigrateLegacyAsset(FolderStructureSettings settings)
        {
            FolderStructureSettings legacy = AssetDatabase.LoadAssetAtPath<FolderStructureSettings>(LegacyAssetPath);
            if (legacy == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:FolderStructureSettings");
                if (guids.Length == 0) return false;
                legacy = AssetDatabase.LoadAssetAtPath<FolderStructureSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (legacy == null) return false;
            }

            string json = EditorJsonUtility.ToJson(legacy);
            EditorJsonUtility.FromJsonOverwrite(json, settings);

            string assetPath = AssetDatabase.GetAssetPath(legacy);
            if (!string.IsNullOrEmpty(assetPath))
            {
                // DeleteAsset applies immediately; a blanket SaveAssets() would also commit unrelated
                // dirty assets the user has open.
                AssetDatabase.DeleteAsset(assetPath);
                Debug.Log($"[FolderStructureSettings] Migrated '{assetPath}' → '{SettingsPath}'.");
            }

            return true;
        }

        /// <summary>The starter profile seeded on first launch.</summary>
        public static FolderProfile BuildMtxTemplateProfile()
        {
            return new FolderProfile
            {
                profileName = "MTX Template",
                variableName = "event",
                isExpanded = false,
                quickNavTabName = "{event}",
                folders = new List<FolderEntry>
                {
                    new FolderEntry { root = PathRoot.Assets,      pathTemplate = "mtx/{event}",                 addToQuickNav = true },
                    new FolderEntry { root = PathRoot.Assets,      pathTemplate = "mtx/{event}/Popup" },
                    new FolderEntry { root = PathRoot.Assets,      pathTemplate = "mtx/{event}/Popup/FontAssets" },
                    new FolderEntry { root = PathRoot.Assets,      pathTemplate = "mtx/{event}/Behavior" },
                    new FolderEntry { root = PathRoot.Assets,      pathTemplate = "mtx/{event}/Badge" },
                    new FolderEntry { root = PathRoot.ProjectRoot, pathTemplate = "art/mtx/{event}",             addToQuickNav = true },
                }
            };
        }
    }
}
