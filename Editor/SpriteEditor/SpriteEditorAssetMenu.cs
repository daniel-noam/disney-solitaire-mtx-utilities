using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Project-window entry points for the sprite tools, for when the window would be overkill or
    /// the job spans a whole folder.
    ///
    /// The 9-slice items never rewrite pixels: compressing source art is destructive enough that it
    /// should only happen from the window, where the setting is visible and confirmed. Masks are
    /// always new files, so they are safe to batch.
    /// </summary>
    public static class SpriteEditorAssetMenu
    {
        private const string Root = "Assets/Sprite Editor/";

        [MenuItem(Root + "Open in Sprite Editor", false, 1300)]
        private static void OpenEditor()
        {
            // The window edits one texture at a time, so a multi-selection opens on the first and the
            // rest are left to the batch items below.
            var textures = CollectTextures();
            if (textures.Count == 0) return;

            SpriteEditorWindow.ShowWith(textures[0]);
            if (textures.Count > 1)
            {
                Debug.Log($"{SpriteImage.Log} Opened {Path.GetFileName(textures[0])}. The editor works on " +
                          $"one texture at a time - use the other Assets/Sprite Editor items for the " +
                          $"remaining {textures.Count - 1}.");
            }
        }

        [MenuItem(Root + "Open in Sprite Editor", true)]
        private static bool ValidateOpenEditor()
        {
            return HasCandidateSelection();
        }

        // -------------------------------------------------------------------------------------------
        // Mask
        // -------------------------------------------------------------------------------------------

        [MenuItem(Root + "Create Masks", false, 1301)]
        private static void CreateMasks()
        {
            var options = MaskOptions.Load();
            var paths = CollectTextures();
            if (paths.Count == 0) return;

            var targets = new List<SpriteTarget>(paths.Count);
            foreach (string path in paths) targets.Add(SpriteTarget.FromAssetPath(path));

            // Ask once for the whole batch rather than per file, and let the answer be "leave the
            // ones I have already tweaked alone".
            int existing = 0;
            foreach (var candidate in targets)
                if (File.Exists(options.BuildOutputAbsolutePath(candidate)))
                    existing++;

            bool overwrite = true;
            if (existing > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Replace existing masks?",
                    $"{existing} of the {paths.Count} selected texture(s) already have a mask with the " +
                    $"suffix '{options.suffix}'.",
                    "Replace", "Cancel", "Skip existing");

                if (choice == 1) return;
                overwrite = choice == 0;
            }

            int created = 0;
            int skipped = 0;

            // Deliberately not wrapped in StartAssetEditing: each mask has to be imported before its
            // import settings can be written, and batched editing defers exactly that import.
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var candidate = targets[i];
                    EditorUtility.DisplayProgressBar("Creating masks", candidate.FileName,
                        (float) i / targets.Count);

                    if (MaskGenerator.Export(candidate, null, options, overwrite, out _, out string message))
                    {
                        created++;
                    }
                    else
                    {
                        Debug.LogWarning($"{SpriteImage.Log} {candidate.DisplayPath}: no mask written, {message}");
                        skipped++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            Debug.Log($"{SpriteImage.Log} Created {created} mask(s)" +
                      $"{(skipped > 0 ? $", skipped {skipped}" : "")}.");
        }

        [MenuItem(Root + "Create Masks", true)]
        private static bool ValidateCreateMasks()
        {
            return HasCandidateSelection();
        }

        // -------------------------------------------------------------------------------------------
        // 9-slice
        // -------------------------------------------------------------------------------------------

        [MenuItem(Root + "Detect and Apply 9-Slice Borders", false, 1320)]
        private static void DetectAndApply()
        {
            var options = NineSliceOptions.Load();

            // Force "set the border on the original, write no image", whatever the window's saved
            // preference happens to be. BOTH flags are needed: borderOnly alone still leaves
            // overwriteOriginal off, which targets a new sibling file - so this menu would have
            // duplicated every selected texture instead of bordering it in place.
            options.borderOnly = true;
            options.overwriteOriginal = true;

            var paths = CollectTextures();
            int applied = 0;
            int skipped = 0;

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    EditorUtility.DisplayProgressBar("9-Slice", Path.GetFileName(path), (float) i / paths.Count);

                    if (!NineSliceApplier.CanSlice(path, out string reason))
                    {
                        Debug.LogWarning($"{SpriteImage.Log} {path}: {reason}");
                        skipped++;
                        continue;
                    }

                    NineSliceBorder border;
                    using (var snapshot = SpriteImage.Load(path, out string error))
                    {
                        if (snapshot == null)
                        {
                            Debug.LogWarning($"{SpriteImage.Log} {path}: {error}");
                            skipped++;
                            continue;
                        }

                        border = NineSliceAnalyzer.Detect(snapshot, options).Border;
                    }

                    if (NineSliceApplier.Apply(path, border, options, out string message))
                    {
                        Debug.Log($"{SpriteImage.Log} {path}: {message}",
                            AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                        applied++;
                    }
                    else
                    {
                        Debug.LogWarning($"{SpriteImage.Log} {path}: {message}");
                        skipped++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"{SpriteImage.Log} Detected and applied borders on {applied} texture(s)" +
                      $"{(skipped > 0 ? $", skipped {skipped}" : "")}.");
        }

        [MenuItem(Root + "Detect and Apply 9-Slice Borders", true)]
        private static bool ValidateDetectAndApply()
        {
            return HasCandidateSelection();
        }

        [MenuItem(Root + "Clear 9-Slice Borders", false, 1321)]
        private static void ClearBorders()
        {
            var paths = CollectTextures();
            int cleared = 0;
            foreach (string path in paths)
            {
                if (NineSliceApplier.ClearBorder(path, out string message)) cleared++;
                else Debug.LogWarning($"{SpriteImage.Log} {path}: {message}");
            }

            Debug.Log($"{SpriteImage.Log} Cleared the border on {cleared} texture(s).");
        }

        [MenuItem(Root + "Clear 9-Slice Borders", true)]
        private static bool ValidateClearBorders()
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
