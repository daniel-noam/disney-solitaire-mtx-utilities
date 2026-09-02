using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// The three chores that surround finished art rather than change it: bleeding colour out behind
    /// the transparent edge, cropping the empty margin, and rounding the canvas up to a power of two.
    ///
    /// They share a tab because they share a shape - every visible pixel survives, only the frame
    /// around it moves - and because you usually want more than one of them at once.
    /// </summary>
    [Serializable]
    public class CleanupTool : SpriteEditorTool
    {
        [SerializeField] private CleanupOptions options;
        [SerializeField] private bool showSource;
        [SerializeField] private bool hasBackup;

        /// <summary>Sprite sheet, so its sub-sprite rects would not survive a trim or a pad.</summary>
        [SerializeField] private bool isSpriteSheet;

        [NonSerialized] private Texture2D preview;
        [NonSerialized] private int previewSignature = -1;
        [NonSerialized] private bool hasPendingPrefsSave;

        // Working out the plan means scanning every pixel for the content bounds, and three separate
        // things want it per repaint - the preview, the outline over it and the info box.
        [NonSerialized] private CleanupPlan cachedPlan;
        [NonSerialized] private int planSignature = -1;

        public override string DisplayName => "Cleanup";

        private CleanupOptions Options
        {
            get
            {
                if (options == null) options = CleanupOptions.Load();
                return options;
            }
        }

        public override void OnTargetChanged()
        {
            previewSignature = -1;
            planSignature = -1;
            var current = Target;
            hasBackup = current != null && !current.IsExternal && SpriteBackups.Has(current.assetPath);
            isSpriteSheet = current != null && !current.IsExternal &&
                            AssetImporter.GetAtPath(current.assetPath) is TextureImporter importer &&
                            importer.spriteImportMode == SpriteImportMode.Multiple;
        }

        public override void FlushPreferences()
        {
            if (!hasPendingPrefsSave || options == null) return;
            options.Save();
            hasPendingPrefsSave = false;
        }

        public override void OnDisable()
        {
            FlushPreferences();
            ReleasePreview();
        }

        public override string GetWarning()
        {
            var current = Target;
            if (current == null) return null;

            // A refusal outranks an explanation: this one says Apply will not run at all.
            if (Snapshot != null && CurrentPlan.ChangesGeometry && isSpriteSheet)
                return "Sprite Mode is Multiple. Trimming or padding would leave every sub-sprite rect "
                       + "pointing at the wrong pixels, so Apply will refuse — turn them off and bleed on "
                       + "its own, which moves nothing.";

            if (Snapshot != null && !Snapshot.IsSourceResolution)
                return "Pixels were read back from the imported texture because this file format cannot be " +
                       "decoded directly, so the result would be written at the imported size rather than " +
                       "the source file's. Convert the file to .png first.";

            if (!current.IsExternal && !current.isSprite)
                return $"Texture Type is {current.textureTypeName}, not Sprite. Trimming or padding moves " +
                       "the image's coordinates, which is wrong for an atlas page (Spine, TMP) or a tiled " +
                       "material texture. Bleeding on its own is safe.";

            return null;
        }

        // -----------------------------------------------------------------------------------------
        // Preview
        // -----------------------------------------------------------------------------------------

        public override void DrawToolbar()
        {
            bool picked = GUILayout.Toggle(showSource, new GUIContent("Source", "Show the original image " +
                    "instead of the result. Compare the edges on a single colour channel: bleed shows " +
                    "there, and in alpha it does not."),
                EditorStyles.toolbarButton, GUILayout.Width(56));
            if (picked == showSource) return;
            showSource = picked;
            Window.Repaint();
        }

        public override void DrawPreview(Rect view)
        {
            var snapshot = Snapshot;
            EnsurePreview();

            bool cleaned = !showSource && preview != null;
            var texture = cleaned ? preview : snapshot.Texture;
            var imageRect = Window.ComputeImageRect(view, texture.width, texture.height, out float scale);

            Window.DrawBackdrop(imageRect);
            Window.DrawImage(imageRect, texture, Color.white,
                scale >= 1f ? FilterMode.Point : FilterMode.Bilinear);
            SpriteEditorWindow.DrawOutline(imageRect, SpriteEditorWindow.BoundsColor);

            // Where the source used to end, so a trim or a pad is visible rather than merely stated.
            if (cleaned) DrawSourceOutline(imageRect, scale);

            Window.HandleNavigation(view, imageRect, scale);
        }

        /// <summary>Outlines the part of the result that came from the source image.</summary>
        private void DrawSourceOutline(Rect imageRect, float scale)
        {
            var plan = CurrentPlan;
            if (!plan.ChangesGeometry) return;

            // Y is measured up from the bottom in texture space and down from the top on screen.
            float x = imageRect.x + plan.OffsetX * scale;
            float height = plan.Crop.height * scale;
            float y = imageRect.yMax - (plan.OffsetY * scale + height);

            SpriteEditorWindow.DrawOutline(new Rect(x, y, plan.Crop.width * scale, height),
                SpriteEditorWindow.GuideColor);
        }

        private void EnsurePreview()
        {
            var snapshot = Snapshot;
            if (snapshot == null)
            {
                ReleasePreview();
                return;
            }

            int signature = Signature(Options);
            if (preview != null && previewSignature == signature) return;

            ReleasePreview();
            preview = CleanupProcessor.CreateTexture(snapshot, Options, CurrentPlan);
            previewSignature = signature;
        }

        /// <summary>The plan for the current settings, worked out once and kept until they change.</summary>
        private CleanupPlan CurrentPlan
        {
            get
            {
                if (Snapshot == null) return default;

                int signature = Signature(Options);
                if (planSignature != signature)
                {
                    cachedPlan = CleanupProcessor.Plan(Snapshot, Options);
                    planSignature = signature;
                }

                return cachedPlan;
            }
        }

        /// <summary>Everything that changes the result. Output settings are deliberately absent.</summary>
        private static int Signature(CleanupOptions options)
        {
            int signature = options.bleed ? options.bleedPasses : 0;
            signature = signature * 397 ^ (options.trim ? options.trimAlpha * 397 ^ options.trimMargin : -1);
            signature = signature * 397 ^ (int) options.padding;
            return (signature * 397 ^ (int) options.anchor) & 0x7FFFFFFF;
        }

        private void ReleasePreview()
        {
            if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
            preview = null;
        }

        // -----------------------------------------------------------------------------------------
        // Options
        // -----------------------------------------------------------------------------------------

        public override void DrawOptions()
        {
            var settings = Options;
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("EDGES", ToolStyles.ColumnHeader);
            settings.bleed = EditorGUILayout.Toggle(
                new GUIContent("Bleed Colour", "Push the visible colour outwards into the transparent " +
                                               "pixels around it. Alpha is untouched, so nothing looks " +
                                               "different until filtering samples across the edge - " +
                                               "which is exactly where the dark fringe comes from."),
                settings.bleed);

            using (new ToolStyles.DisabledScope(!settings.bleed))
            {
                EditorGUI.indentLevel++;
                settings.bleedPasses = EditorGUILayout.IntSlider(
                    new GUIContent("Distance", "How many pixels the colour is pushed out."),
                    settings.bleedPasses, 1, 32);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            GUILayout.Label("CANVAS", ToolStyles.ColumnHeader);

            settings.trim = EditorGUILayout.Toggle(
                new GUIContent("Trim Margin", "Crop the fully transparent border away. The pivot moves " +
                                              "with it, so the sprite stays where it was."),
                settings.trim);

            using (new ToolStyles.DisabledScope(!settings.trim))
            {
                EditorGUI.indentLevel++;
                settings.trimAlpha = EditorGUILayout.IntSlider(
                    new GUIContent("Keep Above", "Alpha at or above this counts as content."),
                    settings.trimAlpha, 1, 255);
                settings.trimMargin = EditorGUILayout.IntField(
                    new GUIContent("Leave Margin", "Transparent pixels kept around the content."),
                    settings.trimMargin);
                EditorGUI.indentLevel--;
            }

            settings.padding = (CleanupPadding) EditorGUILayout.EnumPopup(
                new GUIContent("Pad To", "Round the canvas up, for formats and hardware that want a " +
                                         "power of two, or a compressor that wants a multiple of 4."),
                settings.padding);

            using (new ToolStyles.DisabledScope(settings.padding == CleanupPadding.None))
            {
                EditorGUI.indentLevel++;
                settings.anchor = (CleanupAnchor) EditorGUILayout.EnumPopup(
                    new GUIContent("Anchor", "Where the art sits in the larger canvas."), settings.anchor);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            GUILayout.Label("OUTPUT", ToolStyles.ColumnHeader);

            settings.overwriteOriginal = EditorGUILayout.Toggle(
                new GUIContent("Overwrite Original", "On: replace the file, so everything already " +
                                                     "pointing at it gets the fix. Off: write a new file " +
                                                     "beside it and leave the original alone."),
                settings.overwriteOriginal);

            EditorGUI.indentLevel++;

            using (new ToolStyles.DisabledScope(settings.Overwrites))
            {
                settings.newFileSuffix = EditorGUILayout.TextField(
                    new GUIContent("New File Suffix", "Added to the name of the new file, which is " +
                                                      "written to the same folder as the original."),
                    settings.newFileSuffix);
            }

            using (new ToolStyles.DisabledScope(!settings.Overwrites))
            {
                settings.createBackup = EditorGUILayout.Toggle(
                    new GUIContent("Backup Original", $"Copy the file to {SpriteBackups.FolderLabel} " +
                                                      "before overwriting it. Required for Restore."),
                    settings.createBackup);
            }

            EditorGUI.indentLevel--;

            if (EditorGUI.EndChangeCheck())
            {
                settings.Validate();
                hasPendingPrefsSave = true;
            }

        }

        /// <summary>Anchored above the buttons by the window, so it never scrolls out of view.</summary>
        public override string Summary => DescribeOutput();

        private string DescribeOutput()
        {
            var current = Target;
            var settings = Options;
            if (current == null) return "No image loaded.";
            if (settings.DoesNothing) return "Nothing is turned on, so there is nothing to write.";
            if (Snapshot == null) return "This image's pixels could not be read.";

            // Just the numbers. Where it lands, whether a backup is taken and what moves with the
            // canvas are all decided in the options above, and the refusal is the warning above this.
            var plan = CurrentPlan;
            return plan.ChangesGeometry
                ? $"{Snapshot.Width} x {Snapshot.Height}  ->  {plan.OutputWidth} x {plan.OutputHeight}"
                : $"{Snapshot.Width} x {Snapshot.Height}, unchanged";
        }

        // -----------------------------------------------------------------------------------------
        // Actions
        // -----------------------------------------------------------------------------------------

        public override void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            using (new ToolStyles.DisabledScope(Options.DoesNothing || Snapshot == null))
            {
                // Deferred: this opens a dialog and reimports assets, neither of which belongs in
                // the middle of a layout pass.
                if (GUILayout.Button("Apply", ToolStyles.Primary, GUILayout.Height(ToolStyles.ActionHeight))) Window.Defer(Apply);
            }

            using (new ToolStyles.DisabledScope(!hasBackup))
            {
                if (GUILayout.Button(new GUIContent("Restore", "Put back the file and import settings " +
                                                               "saved before the first overwrite."),
                        ToolStyles.Secondary, GUILayout.Height(ToolStyles.ActionHeight),
                        GUILayout.Width(ToolStyles.ButtonS)))
                    Window.Defer(Restore);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void Apply()
        {
            var current = Target;
            if (current == null) return;
            if (!ConfirmOverwrite(current)) return;

            bool applied = CleanupProcessor.Export(current, Snapshot, Options,
                out string createdPath, out string message);

            if (!applied)
            {
                Debug.LogWarning($"{SpriteImage.Log} {current.DisplayPath}: not cleaned, {message}",
                    current.asset);
                Window.Repaint();
                return;
            }

            Debug.Log($"{SpriteImage.Log} {current.DisplayPath}: {message}",
                current.IsExternal ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(createdPath));

            // Overwriting changed the pixels under us; anything else left them alone.
            if (Options.Overwrites) Window.ReloadTarget();
            else Window.Repaint();
        }

        private void Restore()
        {
            var current = Target;
            if (current == null || current.IsExternal) return;

            if (SpriteBackups.Restore(current.assetPath, out string message))
                Debug.Log($"{SpriteImage.Log} {current.DisplayPath}: restored, {message}", current.asset);
            else
                Debug.LogWarning($"{SpriteImage.Log} {current.DisplayPath}: not restored, {message}",
                    current.asset);

            Window.ReloadTarget();
        }

        /// <summary>
        /// Only the overwriting mode asks. Writing a new file beside the original destroys nothing,
        /// so a confirmation there would be noise.
        /// </summary>
        private bool ConfirmOverwrite(SpriteTarget current)
        {
            var settings = Options;
            if (!settings.Overwrites) return true;

            string backup = current.IsExternal
                ? "This file is outside the project, so there is no backup and no way back."
                : settings.createBackup
                    ? $"A copy of the original goes to {SpriteBackups.FolderLabel} next to Assets/, and " +
                      "Restore puts it back."
                    : "Backups are OFF - the original cannot be recovered by this tool.";

            return EditorUtility.DisplayDialog(
                "Overwrite the original?",
                $"{Path.GetFileName(current.absolutePath)} will be replaced with the cleaned image.\n\n" +
                $"{backup}\n\nTurn Overwrite Original off to write a new file and keep the original.",
                "Overwrite", "Cancel");
        }
    }
}
