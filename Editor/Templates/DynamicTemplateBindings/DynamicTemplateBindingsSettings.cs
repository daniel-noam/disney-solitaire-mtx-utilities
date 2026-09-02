using System;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Which parts of the bindings inspector are switched on. Persisted per user via EditorPrefs -
    /// these are working preferences rather than project data, so they deliberately do not live in
    /// ProjectSettings/ or in an asset, and nothing here changes what is saved into a prefab.
    ///
    /// Read from property drawers, which run far more often than a window does, so the loaded copy
    /// is held in a static rather than re-read from EditorPrefs per row.
    /// </summary>
    [Serializable]
    public class DynamicTemplateBindingsSettings
    {
        private const string PrefsKey = "Tools.Editor.EditorUtilities.DynamicTemplateBindings.Settings";

        [Tooltip("The box at the top listing every key that is missing, unused or duplicated.")]
        public bool showSummary = true;

        [Tooltip("The '(3 refs)' after a key's label, saying how many graph nodes use it.")]
        public bool showRefCounts = true;

        [Tooltip("The warning icon beside a key that has something wrong with it.")]
        public bool showInlineIssues = true;

        [Tooltip("Shade every other row, so a key and its value read as one entry.")]
        public bool stripeRows = true;

        [Tooltip("The 'Rename key and graph references' entry on a key's right-click menu. This " +
                 "rewrites every node in the graph that mentions the key, so it does not depend " +
                 "on any of the reports above.")]
        public bool renameMenuItem = true;

        private static DynamicTemplateBindingsSettings instance;

        /// <summary>The loaded copy. Statics reset on domain reload, which is when this reloads.</summary>
        public static DynamicTemplateBindingsSettings Instance => instance ?? (instance = Load());

        /// <summary>
        /// Whether the graph is worth walking, which is simply whether anything is left that reads
        /// the result. Derived rather than switched: a master toggle can be set to disagree with
        /// the reports under it — scanning for nobody, or a report ticked that cannot draw — and
        /// neither state is one anybody means to be in.
        /// </summary>
        public bool AnalysesReferences => showSummary || showRefCounts || showInlineIssues;

        public void Save()
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
        }

        public void ResetToDefaults()
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(new DynamicTemplateBindingsSettings()), this);
        }

        private static DynamicTemplateBindingsSettings Load()
        {
            var settings = new DynamicTemplateBindingsSettings();
            string json = EditorPrefs.GetString(PrefsKey, null);
            if (string.IsNullOrEmpty(json)) return settings;

            try
            {
                JsonUtility.FromJsonOverwrite(json, settings);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[DynamicTemplateBindings] Could not read saved settings, using " +
                                 "defaults: " + exception.Message);
                settings = new DynamicTemplateBindingsSettings();
            }

            return settings;
        }
    }
}
