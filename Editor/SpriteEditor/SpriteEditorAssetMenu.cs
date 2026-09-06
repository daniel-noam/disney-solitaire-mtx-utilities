using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// The Project-window way into the sprite tools: pick a texture, open it in the window.
    ///
    /// Only the one item. Everything the window does either rewrites pixels or writes new files, and
    /// that belongs where the settings behind it are visible and confirmed - not behind a right-click
    /// on a selection whose contents nobody has looked at.
    /// </summary>
    public static class SpriteEditorAssetMenu
    {
        private const string Item = "Assets/Open in Sprite Editor";

        [MenuItem(Item, false, 1300)]
        private static void OpenEditor()
        {
            // The window edits one texture at a time, so a multi-selection opens on the first.
            var textures = CollectTextures();
            if (textures.Count == 0) return;

            SpriteEditorWindow.ShowWith(textures[0]);
            if (textures.Count > 1)
            {
                Debug.Log($"{SpriteImage.Log} Opened {Path.GetFileName(textures[0])}. The editor works on " +
                          $"one texture at a time, so the other {textures.Count - 1} were not opened.");
            }
        }

        [MenuItem(Item, true)]
        private static bool ValidateOpenEditor()
        {
            return HasCandidateSelection();
        }

        // -------------------------------------------------------------------------------------------
        // Selection
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Cheap enough to run on every menu open: it only looks at what is selected, without
        /// walking the contents of selected folders.
        /// </summary>
        private static bool HasCandidateSelection()
        {
            foreach (var item in Selection.objects)
            {
                if (item is Texture2D) return true;
                string path = AssetDatabase.GetAssetPath(item);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path)) return true;
            }

            return false;
        }

        private static List<string> CollectTextures()
        {
            var paths = new List<string>();
            var seen = new HashSet<string>();

            foreach (var item in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(item);
                if (string.IsNullOrEmpty(path)) continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path }))
                    {
                        string found = AssetDatabase.GUIDToAssetPath(guid);
                        if (seen.Add(found)) paths.Add(found);
                    }
                }
                else if (item is Texture2D && seen.Add(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
    }
}
