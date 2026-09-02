using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Repaints a sprite in one flat colour, keeping its silhouette, and writes the result out as a
    /// new texture - the mask, glow plate, outline or shadow layer that would otherwise mean a round
    /// trip through Photoshop for what is, in the end, "make all of this white".
    ///
    /// The source is never touched: the mask is always a new .png beside it.
    /// </summary>
    [Serializable]
    public class MaskTool : SpriteEditorTool
    {
        [SerializeField] private MaskOptions options;
        [SerializeField] private bool showSource;

        /// <summary>Last mask written this session, for the Select button. Path, not a reference,
        /// so it survives the reimport that follows writing it.</summary>
        [SerializeField] private string lastCreatedPath = string.Empty;

        [NonSerialized] private Texture2D preview;

        /// <summary>
        /// The settings the cached preview was built from - shape only, because the colour is
        /// applied while drawing rather than baked in. -1 forces the first build.
        /// </summary>
        [NonSerialized] private int previewSignature = -1;

        [NonSerialized] private bool hasPendingPrefsSave;

        public override string DisplayName => "Mask";

        private MaskOptions Options
        {
            get
            {
                if (options == null) options = MaskOptions.Load();
                return options;
            }
        }

        public override void OnTargetChanged()
        {
            previewSignature = -1;
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

            // A refusal outranks an explanation: this one says the mask cannot be written at all.
            if (current != null && Options.BuildOutputPath(current) == current.DisplayPath)
                return "The mask would land on the source image itself. Change the suffix.";

            if (current != null && current.IsExternal)
                return "This file is outside the project, so the mask is written to disk without being "
                       + "imported.";

            if (Snapshot != null && !Snapshot.IsSourceResolution)
                return "Pixels were read back from the imported texture because this file format cannot be " +
                       $"decoded directly, so the mask will be {Snapshot.Width} x {Snapshot.Height} - the " +
                       "imported size, which may be smaller than the source file.";

            return null;
        }

        // -----------------------------------------------------------------------------------------
        // Preview
        // -----------------------------------------------------------------------------------------

        public override void DrawToolbar()
        {
            bool picked = GUILayout.Toggle(showSource, new GUIContent("Source", "Show the original texture " +
                                                                                "instead of the mask."),
                EditorStyles.toolbarButton, GUILayout.Width(56));
            if (picked == showSource) return;
            showSource = picked;
            Window.Repaint();
        }

        public override void DrawPreview(Rect view)
        {
            var snapshot = Snapshot;
            EnsurePreview();

            var imageRect = Window.ComputeImageRect(view, snapshot.Width, snapshot.Height, out float scale);
            bool masked = !showSource && preview != null;
            var texture = masked ? preview : snapshot.Texture;

            Window.DrawBackdrop(imageRect);

            // The preview is built white and tinted at draw time, so dragging the colour picker
            // never rebuilds it.
            Window.DrawImage(imageRect, texture, masked ? Options.color : Color.white,
                scale >= 1f ? FilterMode.Point : FilterMode.Bilinear);
            SpriteEditorWindow.DrawOutline(imageRect, SpriteEditorWindow.BoundsColor);

            Window.HandleNavigation(view, imageRect, scale);
        }

        /// <summary>
        /// Rebuilds the mask texture when the shape settings or the source moved under it. One pass
        /// over the pixels is cheap enough to do on demand, but not on every repaint - and on a
        /// 4K texture, not on every frame of a slider drag either.
        /// </summary>
        private void EnsurePreview()
        {
            var snapshot = Snapshot;
            if (snapshot == null)
            {
                ReleasePreview();
                return;
            }

            int signature = ShapeSignature(Options);
            if (preview != null && previewSignature == signature &&
                preview.width == snapshot.Width && preview.height == snapshot.Height) return;

            ReleasePreview();
            preview = MaskGenerator.CreateTexture(snapshot, Options, Color.white);
            previewSignature = signature;
        }

        /// <summary>
        /// Everything that changes which pixels the mask covers. The colour is deliberately absent:
        /// it is applied when drawing, so changing it does not invalidate the preview.
        /// </summary>
        private static int ShapeSignature(MaskOptions options)
        {
            int signature = (int) options.shape * 397 ^ (int) options.edges;
            signature = signature * 397 ^ options.threshold;
            signature = signature * 397 ^ (options.invert ? 1 : 0);
            signature = signature * 397 ^ options.grow;
            return (signature * 397 ^ (options.outlineOnly ? 1 : 0)) & 0x7FFFFFFF;
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

            GUILayout.Label("MASK", ToolStyles.ColumnHeader);

            EditorGUILayout.BeginHorizontal();
            settings.color = EditorGUILayout.ColorField(
                new GUIContent("Color", "Every pixel of the mask takes this colour. Its alpha scales " +
                                        "the whole mask, so a half-transparent colour gives a half-" +
                                        "strength mask."),
                settings.color);
            // A 22-pixel-wide button with no height of its own is a hard thing to hit; these are
            // now a proper control tall and an icon button wide.
            if (GUILayout.Button(new GUIContent("W", "White"), ToolStyles.SecondaryCompact,
                    GUILayout.Width(ToolStyles.IconWidth), GUILayout.Height(ToolStyles.ControlHeight)))
                settings.color = Color.white;
            if (GUILayout.Button(new GUIContent("B", "Black"), ToolStyles.SecondaryCompact,
                    GUILayout.Width(ToolStyles.IconWidth), GUILayout.Height(ToolStyles.ControlHeight)))
                settings.color = Color.black;
            EditorGUILayout.EndHorizontal();

            settings.shape = (MaskShape) EditorGUILayout.EnumPopup(
                new GUIContent("Shape From", "Alpha keeps the sprite's silhouette; Luminance masks the " +
                                             "bright parts of grey-scale art; Everything fills the whole " +
                                             "rectangle, transparent pixels included."),
                settings.shape);

            settings.edges = (MaskEdges) EditorGUILayout.EnumPopup(
                new GUIContent("Edges", "Keep leaves antialiased edges soft; Threshold snaps every pixel " +
                                        "to fully on or fully off."),
                settings.edges);

            // Always drawn and merely disabled, never hidden: changing the count of controls part-way
            // through a frame is what breaks IMGUI layout groups.
            using (new ToolStyles.DisabledScope(settings.edges != MaskEdges.Threshold))
            {
                EditorGUI.indentLevel++;
                settings.threshold = EditorGUILayout.IntSlider(
                    new GUIContent("Threshold", "Coverage at or above this counts as solid."),
                    settings.threshold, 1, 255);
                EditorGUI.indentLevel--;
            }

            settings.invert = EditorGUILayout.Toggle(
                new GUIContent("Invert", "Mask everything the sprite does not cover instead."),
                settings.invert);

            settings.grow = EditorGUILayout.IntSlider(
                new GUIContent("Grow", "Expand the shape outwards by this many pixels - a glow or " +
                                       "shadow plate that sits proud of the art."),
                settings.grow, 0, 64);

            using (new ToolStyles.DisabledScope(settings.grow <= 0))
            {
                EditorGUI.indentLevel++;
                settings.outlineOnly = EditorGUILayout.Toggle(
                    new GUIContent("Outline Only", "Keep only the ring the growth added, so the result " +
                                                   "is an outline rather than a fattened silhouette."),
                    settings.outlineOnly);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            GUILayout.Label("OUTPUT", ToolStyles.ColumnHeader);

            settings.suffix = EditorGUILayout.TextField(
                new GUIContent("Suffix", "Added to the source's file name. The mask is written to the " +
                                         "same folder, and is always a .png, because the shape lives " +
                                         "in the alpha channel."),
                settings.suffix);

            settings.copyImportSettings = EditorGUILayout.Toggle(
                new GUIContent("Copy Import Settings", "Copy pivot, pixels per unit and the 9-slice border " +
                                                       "from the source, so the mask lines up with it."),
                settings.copyImportSettings);

            // The preview notices a changed shape by itself, through its signature, so nothing here
            // has to invalidate it.
            if (EditorGUI.EndChangeCheck())
            {
                settings.Validate();
                hasPendingPrefsSave = true;
            }

        }


        /// <summary>Spells out exactly which file the button is about to write.</summary>

        // -----------------------------------------------------------------------------------------
        // Actions
        // -----------------------------------------------------------------------------------------

        public override void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            // Deferred: this opens a dialog and reimports assets, neither of which belongs in the
            // middle of a layout pass.
            if (GUILayout.Button("Create Mask", ToolStyles.Primary, GUILayout.Height(ToolStyles.ActionHeight))) Window.Defer(Create);

            using (new ToolStyles.DisabledScope(string.IsNullOrEmpty(lastCreatedPath)))
            {
                if (GUILayout.Button(new GUIContent("Select", "Show the last mask this window wrote in " +
                                                              "the Project window."),
                        ToolStyles.Secondary, GUILayout.Height(ToolStyles.ActionHeight),
                        GUILayout.Width(ToolStyles.ButtonS)))
                    SelectLastCreated();
            }

            EditorGUILayout.EndHorizontal();

        }

        private void Create()
        {
            var current = Target;
            if (current == null) return;

            // Asked rather than assumed, with the suffix's answer as the default — the panel's own
            // overwrite prompt then replaces the one this used to raise itself.
            string suggested = Options.BuildOutputAbsolutePath(current);
            string picked = EditorUtility.SaveFilePanel("Save mask as",
                Path.GetDirectoryName(suggested) ?? Application.dataPath,
                Path.GetFileName(suggested), "png");

            if (string.IsNullOrEmpty(picked)) return;

            // Written back as a project path when it lands under Assets/, so it is imported rather
            // than left as a loose file the project cannot see.
            string destinationOverride = SpriteImage.ToAssetPath(picked) ?? picked;
            bool exists = File.Exists(picked);

            bool created = MaskGenerator.Export(current, Snapshot, Options, exists,
                destinationOverride, out string createdPath, out string message);

            if (created)
            {
                // Only a mask inside the project can be pinged, so Select stays disabled for one
                // written next to a file on disk.
                lastCreatedPath = current.IsExternal ? string.Empty : createdPath;
                Debug.Log($"{SpriteImage.Log} {current.DisplayPath}: {message}",
                    current.IsExternal ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(createdPath));
            }
            else
            {
                Debug.LogWarning($"{SpriteImage.Log} {current.DisplayPath}: no mask written, {message}",
                    current.asset);
            }

            Window.Repaint();
        }

        private void SelectLastCreated()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(lastCreatedPath);
            if (asset == null)
            {
                // Deleted or renamed since it was written; forget it rather than leaving a button
                // that does nothing.
                lastCreatedPath = string.Empty;
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
