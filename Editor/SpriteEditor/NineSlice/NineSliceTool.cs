using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Auto-detects a texture's stretchable band, lets the border be corrected by dragging the
    /// guides, checks it against a stretch test, then writes it - optionally cutting the redundant
    /// centre pixels out of the image.
    ///
    /// Replaces the "make a ScriptableObject in the folder you want processed and press Run" flow,
    /// which gave no preview, no manual control and always rewrote the source art.
    /// </summary>
    [Serializable]
    public class NineSliceTool : SpriteEditorTool
    {
        private const float HandleGrabSize = 11f;

        private enum PreviewTab
        {
            SliceBorder = 0,
            StretchTest = 1,
        }

        private enum BorderHandle
        {
            None = 0,
            Left,
            Right,
            Bottom,
            Top,

            /// <summary>The stretchable middle, dragged to move all four borders together.</summary>
            Centre,
        }

        // Where the middle drag started, so translating stays exact instead of accumulating a
        // rounding error per frame.
        [NonSerialized] private Vector2 centreDragOrigin;
        [NonSerialized] private NineSliceBorder centreDragBorder;

        [SerializeField] private NineSliceOptions options;
        [SerializeField] private PreviewTab tab = PreviewTab.SliceBorder;
        [SerializeField] private Vector2Int stretchSize = new Vector2Int(256, 128);
        [SerializeField] private bool showStretchGuides = true;

        /// <summary>Border being edited, in source-texture pixels.</summary>
        [SerializeField] private NineSliceBorder border;

        /// <summary>Border currently written on the importer, for the "not applied yet" hint.</summary>
        [SerializeField] private NineSliceBorder appliedBorder;

        [SerializeField] private bool manual;
        [SerializeField] private bool sliceable = true;
        [SerializeField] private bool hasBackup;

        /// <summary>
        /// Why this texture cannot take a border, when it cannot. Written once when the texture is
        /// loaded and never touched again - it used to share a general-purpose "note" field that
        /// detection and Apply also wrote to, which meant the real reason got overwritten before it
        /// could be shown.
        /// </summary>
        [SerializeField] private string blockedReason = string.Empty;

        /// <summary>
        /// The guide the arrow keys move. Set by clicking one and kept afterwards, unlike
        /// <see cref="activeHandle"/>, which only lives for the length of a drag.
        /// </summary>
        [SerializeField] private BorderHandle selectedHandle = BorderHandle.None;

        [NonSerialized] private BorderHandle activeHandle = BorderHandle.None;
        [NonSerialized] private bool hasPendingPrefsSave;

        public override string DisplayName => "9-Slice";

        private NineSliceOptions Options
        {
            get
            {
                if (options == null) options = NineSliceOptions.Load();
                return options;
            }
        }

        public override void OnTargetChanged()
        {
            var current = Target;
            if (current == null) return;

            if (current.IsExternal)
            {
                // A border lives on a TextureImporter, and a file outside the project does not have
                // one - only the pixel cut applies here. Detection and the stretch test still work.
                sliceable = NineSliceApplier.CanSlice(current, out string reason);
                blockedReason = sliceable ? string.Empty : reason;
                border = NineSliceBorder.Zero;
                hasBackup = false;
            }
            else
            {
                sliceable = NineSliceApplier.CanSlice(current.assetPath, out string reason);
                blockedReason = sliceable ? string.Empty : reason;
                border = NineSliceApplier.ReadBorder(current.assetPath);
                hasBackup = NineSliceApplier.HasBackup(current.assetPath);
            }

            appliedBorder = border;
            manual = false;

            // A stretch test smaller than a handful of pixels shows nothing, so an unset or stale
            // size starts from twice the texture instead.
            if (Snapshot != null && (stretchSize.x < 8 || stretchSize.y < 8))
                stretchSize = new Vector2Int(Snapshot.Width * 2, Snapshot.Height * 2);

            // Detect straight away unless it already carries a border worth keeping.
            if (Snapshot != null && border.IsZero) Detect(false);
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
        }

        // -----------------------------------------------------------------------------------------
        // Toolbar and warnings
        // -----------------------------------------------------------------------------------------

        public override void DrawToolbar()
        {
            // The two tabs put a different number of controls in this row, so the switch is deferred
            // rather than taking effect half way through the row.
            var selected = (PreviewTab) GUILayout.Toolbar((int) tab, new[] { "Slice Border", "Stretch Test" },
                EditorStyles.toolbarButton, GUILayout.Width(180));
            if (selected != tab) Window.Defer(() => tab = selected);

            if (tab == PreviewTab.SliceBorder) return;

            GUILayout.Space(8);
            GUILayout.Label("Size", ToolStyles.Hint, GUILayout.Width(30));
            stretchSize.x = EditorGUILayout.IntSlider(stretchSize.x, 8, 1024, GUILayout.MaxWidth(220));
            stretchSize.y = EditorGUILayout.IntSlider(stretchSize.y, 8, 1024, GUILayout.MaxWidth(220));
            if (GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(48)) && Snapshot != null)
                stretchSize = new Vector2Int(Snapshot.Width * 2, Snapshot.Height * 2);

            showStretchGuides = GUILayout.Toggle(showStretchGuides, "Guides", EditorStyles.toolbarButton,
                GUILayout.Width(52));
        }

        /// <summary>
        /// Arrow keys nudge the selected guide a pixel at a time, shift ten - the last bit of
        /// precision a mouse drag cannot give you at a low zoom.
        /// </summary>
        public override bool HandleShortcut(Event e)
        {
            if (selectedHandle == BorderHandle.None || Snapshot == null) return false;
            if (tab != PreviewTab.SliceBorder) return false;

            int step = e.shift ? 10 : 1;
            int delta;
            bool horizontal = selectedHandle == BorderHandle.Left || selectedHandle == BorderHandle.Right;

            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: delta = -step; break;
                case KeyCode.RightArrow: delta = step; break;
                case KeyCode.UpArrow: delta = -step; break;
                case KeyCode.DownArrow: delta = step; break;
                default: return false;
            }

            bool pressedHorizontal = e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow;
            if (pressedHorizontal != horizontal) return false;

            int width = Snapshot.Width;
            int height = Snapshot.Height;
            var nudged = border;

            // Every guide moves with the arrow, but left/bottom are measured from the near edge and
            // right/top from the far one, so half of them count the other way.
            switch (selectedHandle)
            {
                case BorderHandle.Left:
                    nudged.left = Mathf.Clamp(nudged.left + delta, 0, Mathf.Max(0, width - 1 - nudged.right));
                    break;
                case BorderHandle.Right:
                    nudged.right = Mathf.Clamp(nudged.right - delta, 0, Mathf.Max(0, width - 1 - nudged.left));
                    break;
                case BorderHandle.Top:
                    nudged.top = Mathf.Clamp(nudged.top + delta, 0, Mathf.Max(0, height - 1 - nudged.bottom));
                    break;
                case BorderHandle.Bottom:
                    nudged.bottom = Mathf.Clamp(nudged.bottom - delta, 0, Mathf.Max(0, height - 1 - nudged.top));
                    break;
            }

            if (nudged.Equals(border)) return true;

            border = nudged;
            manual = true;
            return true;
        }

        public override string GetWarning()
        {
            var current = Target;
            if (current == null) return null;

            if (!sliceable && !string.IsNullOrEmpty(blockedReason)) return blockedReason;

            if (current.IsExternal)
                return "This file is outside the project, so there is no importer to store a border on. " +
                       "Only the pixel cut can be applied here - Border Only does nothing for it.";

            if (!current.IsExternal && !current.isSprite)
                return $"Texture Type is {current.textureTypeName}, not Sprite. Applying will convert it to " +
                       "Sprite, and cutting pixels will change the image - wrong for an atlas page (Spine, " +
                       "TMP) or a tiled material texture, whose coordinates would no longer line up.";

            if (Snapshot != null && !Snapshot.IsSourceResolution)
                return "Pixels were read back from the imported texture because this file format cannot be " +
                       "decoded directly. If import settings downscale it, the detected border will be off " +
                       "by the same factor - check it against the stretch test.";

            return null;
        }

        // -----------------------------------------------------------------------------------------
        // Preview
        // -----------------------------------------------------------------------------------------

        public override void DrawPreview(Rect view)
        {
            if (tab == PreviewTab.SliceBorder) DrawSliceView(view);
            else DrawStretchView(view);
        }

        /// <summary>Source texture with the border guides drawn over it.</summary>
        private void DrawSliceView(Rect view)
        {
            var snapshot = Snapshot;
            var imageRect = Window.ComputeImageRect(view, snapshot.Width, snapshot.Height, out float scale);

            Window.DrawBackdrop(imageRect);
            Window.DrawImage(imageRect, snapshot.Texture, Color.white,
                scale >= 1f ? FilterMode.Point : FilterMode.Bilinear);
            SpriteEditorWindow.DrawOutline(imageRect, SpriteEditorWindow.BoundsColor);

            GuideLines(imageRect, scale, border, out float x0, out float x1, out float y0, out float y1);

            if (x1 > x0 && y1 > y0)
                EditorGUI.DrawRect(new Rect(x0, y0, x1 - x0, y1 - y0), SpriteEditorWindow.CenterFillColor);

            DrawGuide(new Rect(x0 - 0.5f, imageRect.y, 1f, imageRect.height), BorderHandle.Left);
            DrawGuide(new Rect(x1 - 0.5f, imageRect.y, 1f, imageRect.height), BorderHandle.Right);
            DrawGuide(new Rect(imageRect.x, y0 - 0.5f, imageRect.width, 1f), BorderHandle.Top);
            DrawGuide(new Rect(imageRect.x, y1 - 0.5f, imageRect.width, 1f), BorderHandle.Bottom);

            HandleSliceInput(view, imageRect, scale);
        }

        private void DrawGuide(Rect line, BorderHandle handle)
        {
            bool hot = activeHandle == handle || (activeHandle == BorderHandle.None && selectedHandle == handle);
            var colour = hot ? SpriteEditorWindow.GuideHotColor : SpriteEditorWindow.GuideColor;

            bool detected = handle == BorderHandle.Left || handle == BorderHandle.Right
                ? Options.detectHorizontal
                : Options.detectVertical;

            if (detected)
            {
                EditorGUI.DrawRect(line, colour);
                return;
            }

            // An axis Detect will not touch is drawn dashed and dimmer. The border is still there and
            // still draggable by hand — what the dashes say is that pressing Detect will leave this
            // pair exactly where they are, which is otherwise only knowable from the Axes toggles.
            colour.a *= 0.55f;
            DrawDashedLine(line, colour);
        }

        /// <summary>A one-pixel guide broken into dashes, along whichever way it runs.</summary>
        private static void DrawDashedLine(Rect line, Color colour)
        {
            const float dash = 4f;
            const float gap = 3f;

            if (line.height > line.width)
            {
                for (float y = line.y; y < line.yMax; y += dash + gap)
                    EditorGUI.DrawRect(new Rect(line.x, y, line.width,
                        Mathf.Min(dash, line.yMax - y)), colour);
                return;
            }

            for (float x = line.x; x < line.xMax; x += dash + gap)
                EditorGUI.DrawRect(new Rect(x, line.y, Mathf.Min(dash, line.xMax - x),
                    line.height), colour);
        }

        /// <summary>
        /// Screen-space positions of the four guides. Note that <c>border.top</c> counts down from
        /// the texture's top edge while <c>border.bottom</c> counts up from its bottom, matching
        /// spriteBorder - the preview's Y axis runs the other way, hence the two forms below.
        /// </summary>
        private static void GuideLines(Rect imageRect, float scale, NineSliceBorder value,
            out float x0, out float x1, out float y0, out float y1)
        {
            x0 = imageRect.x + value.left * scale;
            x1 = imageRect.xMax - value.right * scale;
            y0 = imageRect.y + value.top * scale;
            y1 = imageRect.yMax - value.bottom * scale;
        }

        private void HandleSliceInput(Rect view, Rect imageRect, float scale)
        {
            var e = Event.current;
            GuideLines(imageRect, scale, border, out float x0, out float x1, out float y0, out float y1);

            float grab = HandleGrabSize;
            EditorGUIUtility.AddCursorRect(new Rect(x0 - grab * 0.5f, imageRect.y, grab, imageRect.height),
                MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(new Rect(x1 - grab * 0.5f, imageRect.y, grab, imageRect.height),
                MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(new Rect(imageRect.x, y0 - grab * 0.5f, imageRect.width, grab),
                MouseCursor.ResizeVertical);
            EditorGUIUtility.AddCursorRect(new Rect(imageRect.x, y1 - grab * 0.5f, imageRect.width, grab),
                MouseCursor.ResizeVertical);

            if (x1 > x0 && y1 > y0)
                EditorGUIUtility.AddCursorRect(new Rect(x0, y0, x1 - x0, y1 - y0), MouseCursor.MoveArrow);

            // Panning and zooming come first: they are the modified drags, so nothing below can
            // swallow them.
            if (Window.HandleNavigation(view, imageRect, scale)) return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!view.Contains(e.mousePosition) || e.button != 0) break;
                    activeHandle = PickHandle(e.mousePosition, imageRect, x0, x1, y0, y1);

                    if (activeHandle == BorderHandle.Centre)
                    {
                        centreDragOrigin = e.mousePosition;
                        centreDragBorder = border;
                    }

                    // Clicking a guide selects it for the arrow keys; clicking anywhere else drops
                    // the selection, so a stray keypress cannot move a guide you forgot about.
                    selectedHandle = activeHandle;
                    if (activeHandle != BorderHandle.None) e.Use();
                    Window.Repaint();
                    break;

                case EventType.MouseDrag:
                    if (activeHandle == BorderHandle.None) break;
                    DragGuide(e, imageRect, scale);
                    e.Use();
                    Window.Repaint();
                    break;

                case EventType.MouseUp:
                    if (activeHandle == BorderHandle.None) break;
                    activeHandle = BorderHandle.None;
                    e.Use();
                    Window.Repaint();
                    break;
            }
        }

        private static BorderHandle PickHandle(Vector2 mouse, Rect imageRect,
            float x0, float x1, float y0, float y1)
        {
            var best = BorderHandle.None;
            float bestDistance = HandleGrabSize * 0.5f;

            // The guides are tested first, so grabbing an edge of the middle still resizes rather
            // than moves — the edge is the more precise intent of the two.

            bool withinRows = mouse.y >= imageRect.y - HandleGrabSize && mouse.y <= imageRect.yMax + HandleGrabSize;
            bool withinColumns = mouse.x >= imageRect.x - HandleGrabSize && mouse.x <= imageRect.xMax + HandleGrabSize;

            if (withinRows)
            {
                float left = Mathf.Abs(mouse.x - x0);
                if (left <= bestDistance)
                {
                    bestDistance = left;
                    best = BorderHandle.Left;
                }

                float right = Mathf.Abs(mouse.x - x1);
                if (right < bestDistance)
                {
                    bestDistance = right;
                    best = BorderHandle.Right;
                }
            }

            if (withinColumns)
            {
                float top = Mathf.Abs(mouse.y - y0);
                if (top < bestDistance)
                {
                    bestDistance = top;
                    best = BorderHandle.Top;
                }

                float bottom = Mathf.Abs(mouse.y - y1);
                if (bottom < bestDistance) best = BorderHandle.Bottom;
            }

            if (best != BorderHandle.None) return best;

            return x1 > x0 && y1 > y0 && new Rect(x0, y0, x1 - x0, y1 - y0).Contains(mouse)
                ? BorderHandle.Centre
                : BorderHandle.None;
        }

        private void DragGuide(Event e, Rect imageRect, float scale)
        {
            var snapshot = Snapshot;
            var dragged = border;
            int width = snapshot.Width;
            int height = snapshot.Height;
            float textureX = (e.mousePosition.x - imageRect.x) / scale;
            float textureYFromTop = (e.mousePosition.y - imageRect.y) / scale;

            // Mirrored drags share the axis, so each side may only take half of it.
            int mirroredMaxX = Mathf.Max(0, (width - 1) / 2);
            int mirroredMaxY = Mathf.Max(0, (height - 1) / 2);

            switch (activeHandle)
            {
                case BorderHandle.Left:
                {
                    int value = Mathf.RoundToInt(textureX);
                    if (e.shift) dragged.left = dragged.right = Mathf.Clamp(value, 0, mirroredMaxX);
                    else dragged.left = Mathf.Clamp(value, 0, Mathf.Max(0, width - 1 - dragged.right));
                    break;
                }

                case BorderHandle.Right:
                {
                    int value = Mathf.RoundToInt(width - textureX);
                    if (e.shift) dragged.left = dragged.right = Mathf.Clamp(value, 0, mirroredMaxX);
                    else dragged.right = Mathf.Clamp(value, 0, Mathf.Max(0, width - 1 - dragged.left));
                    break;
                }

                case BorderHandle.Top:
                {
                    int value = Mathf.RoundToInt(textureYFromTop);
                    if (e.shift) dragged.top = dragged.bottom = Mathf.Clamp(value, 0, mirroredMaxY);
                    else dragged.top = Mathf.Clamp(value, 0, Mathf.Max(0, height - 1 - dragged.bottom));
                    break;
                }

                case BorderHandle.Bottom:
                {
                    int value = Mathf.RoundToInt(height - textureYFromTop);
                    if (e.shift) dragged.top = dragged.bottom = Mathf.Clamp(value, 0, mirroredMaxY);
                    else dragged.bottom = Mathf.Clamp(value, 0, Mathf.Max(0, height - 1 - dragged.top));
                    break;
                }

                case BorderHandle.Centre:
                {
                    // Measured from where the drag began rather than from the last frame, so the box
                    // cannot drift by a pixel per frame as each delta is rounded.
                    //
                    // The four borders keep their sizes and only their positions change, so each
                    // shift is bounded by how much room that side has left: moving right can take at
                    // most what `right` still holds, and gives it all to `left`.
                    int shiftX = Mathf.RoundToInt((e.mousePosition.x - centreDragOrigin.x) / scale);
                    shiftX = Mathf.Clamp(shiftX, -centreDragBorder.left, centreDragBorder.right);

                    // Screen Y runs down: dragging down moves the band away from the top edge, so
                    // `top` grows and `bottom` shrinks.
                    int shiftY = Mathf.RoundToInt((e.mousePosition.y - centreDragOrigin.y) / scale);
                    shiftY = Mathf.Clamp(shiftY, -centreDragBorder.top, centreDragBorder.bottom);

                    dragged.left = centreDragBorder.left + shiftX;
                    dragged.right = centreDragBorder.right - shiftX;
                    dragged.top = centreDragBorder.top + shiftY;
                    dragged.bottom = centreDragBorder.bottom - shiftY;
                    break;
                }
            }

            if (dragged.Equals(border)) return;

            border = dragged;
            manual = true;
        }

        /// <summary>The border applied to a stretched rect, i.e. what the sprite will look like in UI.</summary>
        private void DrawStretchView(Rect view)
        {
            var snapshot = Snapshot;
            float width = Mathf.Min(stretchSize.x, view.width - 16f);
            float height = Mathf.Min(stretchSize.y, view.height - 16f);
            var dest = new Rect(view.center.x - width * 0.5f, view.center.y - height * 0.5f, width, height);

            Window.DrawBackdrop(dest);
            snapshot.Texture.filterMode = FilterMode.Bilinear;

            // Drawn in nine pieces rather than in one go, so the channel filter has to be resolved
            // here instead of inside the window's DrawImage.
            var texture = Window.ResolveDisplayTexture(snapshot.Texture, Color.white, out bool alphaBlend);
            DrawNineSliced(dest, texture, border, snapshot.Width, snapshot.Height, alphaBlend);
            SpriteEditorWindow.DrawOutline(dest, SpriteEditorWindow.BoundsColor);

            if (!showStretchGuides) return;

            float left = Mathf.Min(border.left, dest.width * 0.5f);
            float right = Mathf.Min(border.right, dest.width * 0.5f);
            float top = Mathf.Min(border.top, dest.height * 0.5f);
            float bottom = Mathf.Min(border.bottom, dest.height * 0.5f);
            var guide = SpriteEditorWindow.GuideColor;
            guide.a = 0.5f;
            EditorGUI.DrawRect(new Rect(dest.x + left, dest.y, 1f, dest.height), guide);
            EditorGUI.DrawRect(new Rect(dest.xMax - right, dest.y, 1f, dest.height), guide);
            EditorGUI.DrawRect(new Rect(dest.x, dest.y + top, dest.width, 1f), guide);
            EditorGUI.DrawRect(new Rect(dest.x, dest.yMax - bottom, dest.width, 1f), guide);
        }

        /// <summary>
        /// Draws the texture as nine quads: the corners at 1:1, the edges stretched along one axis,
        /// the centre along both - the same decomposition Unity's Image does with a sliced sprite.
        /// </summary>
        private static void DrawNineSliced(Rect dest, Texture texture, NineSliceBorder value, int sourceWidth,
            int sourceHeight, bool alphaBlend)
        {
            // Borders never overlap, even when the destination is smaller than the border sum.
            float left = Mathf.Min(value.left, dest.width * 0.5f);
            float right = Mathf.Min(value.right, dest.width * 0.5f);
            float top = Mathf.Min(value.top, dest.height * 0.5f);
            float bottom = Mathf.Min(value.bottom, dest.height * 0.5f);

            var columnX = new[] { dest.x, dest.x + left, dest.xMax - right };
            var columnWidth = new[] { left, Mathf.Max(0f, dest.width - left - right), right };

            // Rows run top to bottom in screen space...
            var rowY = new[] { dest.y, dest.y + top, dest.yMax - bottom };
            var rowHeight = new[] { top, Mathf.Max(0f, dest.height - top - bottom), bottom };

            float u = (float) value.left / sourceWidth;
            float uFar = (float) value.right / sourceWidth;
            var columnU = new[] { 0f, u, 1f - uFar };
            var columnUWidth = new[] { u, Mathf.Max(0f, 1f - u - uFar), uFar };

            // ...while UVs run bottom to top, so the row order is reversed here.
            float v = (float) value.top / sourceHeight;
            float vFar = (float) value.bottom / sourceHeight;
            var rowV = new[] { 1f - v, vFar, 0f };
            var rowVHeight = new[] { v, Mathf.Max(0f, 1f - v - vFar), vFar };

            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
            {
                if (columnWidth[column] <= 0f || rowHeight[row] <= 0f) continue;
                if (columnUWidth[column] <= 0f || rowVHeight[row] <= 0f) continue;

                GUI.DrawTextureWithTexCoords(
                    new Rect(columnX[column], rowY[row], columnWidth[column], rowHeight[row]),
                    texture,
                    new Rect(columnU[column], rowV[row], columnUWidth[column], rowVHeight[row]),
                    alphaBlend);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Border fields, options, actions
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The border's numbers and the two buttons that change them, under the preview - they
        /// belong with the guides they move, not with Apply.
        /// </summary>
        public override void DrawBelowPreview()
        {
            EditorGUILayout.BeginHorizontal();
            // The guides' own instructions live on the guides' own row: the hint under the preview
            // is the window's, and says the same thing in every mode.
            GUILayout.Label(new GUIContent("Border",
                    "Drag a guide in the preview to move it  ·  Shift-drag mirrors the opposite edge  ·  "
                    + "Click a guide, then arrow keys nudge it, shift for ten"),
                GUILayout.Width(46));

            var edited = border;
            EditorGUI.BeginChangeCheck();
            edited.left = IntField("L", edited.left);
            edited.bottom = IntField("B", edited.bottom);
            edited.right = IntField("R", edited.right);
            edited.top = IntField("T", edited.top);
            if (EditorGUI.EndChangeCheck() && Snapshot != null)
            {
                border = edited.Clamped(Snapshot.Width, Snapshot.Height);
                manual = true;
            }

            GUILayout.Space(8);

            if (GUILayout.Button(new GUIContent("Detect", "Find the stretchable band and set the border from it."),
                    ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                    GUILayout.Height(ToolStyles.ControlHeight)))
                Detect(true);

            if (GUILayout.Button(new GUIContent("Clear", "Set the border to zero. Press Apply to write it."),
                    ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                    GUILayout.Height(ToolStyles.ControlHeight)))
            {
                border = NineSliceBorder.Zero;
                manual = true;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static int IntField(string label, int value)
        {
            GUILayout.Label(label, ToolStyles.Hint, GUILayout.Width(10));
            return EditorGUILayout.IntField(value, GUILayout.Width(40));
        }

        public override void DrawOptions()
        {
            var settings = Options;
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("DETECTION", ToolStyles.ColumnHeader);
            settings.tolerance = EditorGUILayout.IntSlider(
                new GUIContent("Tolerance", "Largest per-channel difference still treated as 'no change'."),
                settings.tolerance, 0, 64);
            settings.comparison = (NineSliceComparison) EditorGUILayout.EnumPopup(
                new GUIContent("Line Match", "Every Pixel is strict and predictable; Average Difference " +
                                             "copes with dithering and JPEG artefacts."), settings.comparison);

            using (new ToolStyles.DisabledScope(settings.comparison != NineSliceComparison.EveryPixel))
            {
                settings.allowedOutliers = EditorGUILayout.IntField(
                    new GUIContent("Allowed Outliers", "Pixels per line that may break the tolerance."),
                    settings.allowedOutliers);
            }

            settings.margin = EditorGUILayout.IntField(
                new GUIContent("Edge Margin", "Pixels added to the border on each side, so filtering " +
                                              "never samples across a slice boundary."), settings.margin);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Axes");
            // Sized rather than left to fit the text: at the compact style's own size these are a
            // few pixels tall and genuinely awkward to hit.
            settings.detectHorizontal = GUILayout.Toggle(settings.detectHorizontal, "Horizontal",
                ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                GUILayout.Height(ToolStyles.ControlHeight));
            settings.detectVertical = GUILayout.Toggle(settings.detectVertical, "Vertical",
                ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                GUILayout.Height(ToolStyles.ControlHeight));
            EditorGUILayout.EndHorizontal();

            settings.symmetricBorders = EditorGUILayout.Toggle(
                new GUIContent("Symmetric Borders", "Grow the thinner side so left = right and bottom = top."),
                settings.symmetricBorders);
            settings.ignoreTransparentColor = EditorGUILayout.Toggle(
                new GUIContent("Ignore Transparent RGB", "Treat all fully transparent pixels as identical."),
                settings.ignoreTransparentColor);

            EditorGUILayout.Space(6);
            GUILayout.Label("OUTPUT", ToolStyles.ColumnHeader);

            // Two independent switches: one decides whether pixels get cut, the other which asset is
            // written. Neither gates the other, so all four combinations are reachable.
            settings.borderOnly = EditorGUILayout.Toggle(
                new GUIContent("Border Only", "Leave the pixels alone and only set the border. Same " +
                                              "result as dragging the handles in Unity's Sprite Editor, " +
                                              "but with auto-detection."),
                settings.borderOnly);

            settings.overwriteOriginal = EditorGUILayout.Toggle(
                new GUIContent("Overwrite Original", "On: act on the original asset. Off: write a new " +
                                                     "file beside it and leave the original completely " +
                                                     "alone, import settings included."),
                settings.overwriteOriginal);

            // Always drawn and merely disabled, never hidden: changing the count of controls part-way
            // through a frame is what breaks IMGUI layout groups.
            EditorGUI.indentLevel++;

            using (new ToolStyles.DisabledScope(!settings.TargetsNewFile))
            {
                settings.newFileSuffix = EditorGUILayout.TextField(
                    new GUIContent("New File Suffix", "Added to the name of the new file, which is " +
                                                      "written to the same folder as the original."),
                    settings.newFileSuffix);
            }

            using (new ToolStyles.DisabledScope(!settings.CutsPixels))
            {
                settings.centerSize = EditorGUILayout.IntSlider(
                    new GUIContent("Center Size", "Pixels the stretchable centre is cut down to."),
                    settings.centerSize, 1, 32);

                settings.jpgQuality = EditorGUILayout.IntSlider(
                    new GUIContent("JPG Quality", "Only used for .jpg/.jpeg targets."),
                    settings.jpgQuality, 1, 100);
            }

            // Only meaningful for the one combination that can lose pixels.
            using (new ToolStyles.DisabledScope(!settings.Overwrites))
            {
                settings.createBackup = EditorGUILayout.Toggle(
                    new GUIContent("Backup Original", $"Copy the file to {SpriteBackups.FolderLabel} before " +
                                                      "overwriting it. Required for Restore."),
                    settings.createBackup);
            }

            EditorGUI.indentLevel--;

            if (EditorGUI.EndChangeCheck())
            {
                settings.Validate();
                hasPendingPrefsSave = true;

                // Live re-detect, but never throw away guides the user placed by hand.
                if (!manual) Detect(false);
            }

        }

        /// <summary>Anchored above the buttons by the window, so it never scrolls out of view.</summary>
        public override string Summary => DescribeOutput();

        /// <summary>Spells out the exact outcome of the current combination for this texture.</summary>
        private string DescribeOutput()
        {
            var current = Target;
            var settings = Options;
            var snapshot = Snapshot;

            // Says what Apply would do, and no more. That this file is outside the project is
            // already on screen as the warning above the preview — repeating the explanation in the
            // box directly above the button pushed the one sentence that matters out of sight.
            if (current.IsExternal && settings.borderOnly)
                return "Nothing would be written. Turn off Border Only to cut the stretchable "
                       + "centre instead.";

            string newFile = Path.GetFileName(current.SiblingPath(
                NineSliceOptions.SanitizeSuffix(settings.newFileSuffix), Path.GetExtension(current.absolutePath)));

            // The border's numbers are not repeated here: the four fields holding them are a few
            // pixels above this box, and the preview is drawing them. What this box is for is what
            // pressing Apply does with them.
            if (!settings.CutsPixels)
            {
                return settings.TargetsOriginal
                    ? $"Sets the border on {current.FileName}. The image file is not written. " +
                      "Undo it by clearing the border."
                    : $"Copies the image to {newFile} unchanged and sets the border on the copy. " +
                      "The original is not modified at all.";
            }

            if (!SpriteImage.IsRewritableImage(current.absolutePath))
                return $"'{Path.GetExtension(current.absolutePath)}' cannot be re-encoded, so nothing will be " +
                       "cut." + (current.IsExternal ? string.Empty : " Tick Border Only to just set the border.");

            if (snapshot == null) return "This texture's pixels could not be read, so nothing can be cut.";

            var predicted = NineSliceAnalyzer.PredictCompressedSize(
                snapshot.Width, snapshot.Height, border, settings.centerSize);
            int before = snapshot.Width * snapshot.Height;
            int after = predicted.x * predicted.y;

            if (after >= before)
                return "Nothing to cut: the stretchable centre is already " +
                       $"{Mathf.Max(1, snapshot.Width - border.left - border.right)}px wide, at or " +
                       $"below the {settings.centerSize}px target.";

            // Just the numbers. Where it lands and whether a backup is taken are decisions made in
            // the options directly above — restating them here made the one thing this box can say
            // that nothing else does, the size it will end up, the smallest part of it.
            float saved = 100f * (1f - (float) after / before);
            return $"{snapshot.Width} x {snapshot.Height}  ->  {predicted.x} x {predicted.y}   " +
                   $"({saved:0.#}% fewer pixels)";
        }

        public override void DrawActions()
        {
            var current = Target;
            EditorGUILayout.BeginHorizontal();

            // External + Border Only always fails: there is no importer to put a border on, and
            // Border Only leaves the pixels alone too, so there would be nothing left to write.
            bool canApply = sliceable && !(current != null && current.IsExternal && Options.borderOnly);

            // Deferred: these open a dialog, reimport assets, or both - none of which belongs in the
            // middle of a layout pass.
            using (new ToolStyles.DisabledScope(!canApply))
            {
                if (GUILayout.Button("Apply", ToolStyles.Primary, GUILayout.Height(ToolStyles.ActionHeight)))
                    Window.Defer(Apply);
            }

            using (new ToolStyles.DisabledScope(!hasBackup))
            {
                if (GUILayout.Button(new GUIContent("Restore", "Put back the file and border saved " +
                                                               "before the first overwrite."),
                        ToolStyles.Secondary, GUILayout.Height(ToolStyles.ActionHeight),
                        GUILayout.Width(ToolStyles.ButtonS)))
                    Window.Defer(Restore);
            }

            EditorGUILayout.EndHorizontal();

        }

        // -----------------------------------------------------------------------------------------
        // Operations
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A successful detection speaks for itself through the guides and the border fields, so
        /// <paramref name="report"/> only controls whether a *failure* to find a band is logged -
        /// that one needs explaining, since the border silently stays at zero.
        /// </summary>
        private void Detect(bool report)
        {
            if (Target == null || Snapshot == null) return;

            var result = NineSliceAnalyzer.Detect(Snapshot, Options);
            var detected = result.Border;

            // An axis that is not being detected keeps whatever border it already had. The analyzer
            // reports zero for a disabled axis — which is true of what it found, but taking that as
            // the answer wiped that axis's border out of the preview. Unticking Horizontal means
            // "leave the horizontal border alone", not "clear it".
            border = new NineSliceBorder(
                Options.detectHorizontal ? detected.left : border.left,
                Options.detectVertical ? detected.bottom : border.bottom,
                Options.detectHorizontal ? detected.right : border.right,
                Options.detectVertical ? detected.top : border.top);

            manual = false;

            if (report && !result.FoundHorizontal && !result.FoundVertical)
                Debug.LogWarning($"{SpriteImage.Log} {Target.FileName}: {result.Message}");

            Window.Repaint();
        }

        private void Apply()
        {
            var current = Target;
            if (current == null) return;
            if (!ConfirmOverwrite(current)) return;

            bool applied = NineSliceApplier.Apply(current, border, Options, out string message);
            if (!applied)
            {
                Debug.LogWarning($"{SpriteImage.Log} {current.DisplayPath}: {message}", current.asset);
                Window.Repaint();
                return;
            }

            Debug.Log($"{SpriteImage.Log} {current.DisplayPath}: {message}", current.asset);

            // Only the overwriting path changes the pixels we have cached; reloading also re-reads
            // the border and the backup state, which is exactly what was just written.
            if (Options.Overwrites)
            {
                Window.ReloadTarget();
                return;
            }

            // In sibling mode the border went onto the new file and the original was left alone, so
            // reading the original's importer back would return the old value and leave "not applied
            // yet" showing forever. External files never had a border to read back at all.
            appliedBorder = !current.IsExternal && Options.TargetsOriginal
                ? NineSliceApplier.ReadBorder(current.assetPath)
                : border;
            manual = false;
            hasBackup = !current.IsExternal && NineSliceApplier.HasBackup(current.assetPath);

            // Apply may have converted the original to a Sprite, which the header warning reads.
            Window.RefreshTargetInfo();
            Window.Repaint();
        }

        private void Restore()
        {
            var current = Target;
            if (current == null) return;

            bool restored = NineSliceApplier.Restore(current.assetPath, out string message);
            if (restored) Debug.Log($"{SpriteImage.Log} {current.DisplayPath}: restored, {message}", current.asset);
            else Debug.LogWarning($"{SpriteImage.Log} {current.DisplayPath}: not restored, {message}", current.asset);

            // Reloads the pixels and re-reads border, backup state and blocked reason.
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
                    ? $"A copy of the original goes to {SpriteBackups.FolderLabel} next to Assets/, and Restore puts it back."
                    : "Backups are OFF - the original cannot be recovered by this tool.";

            return EditorUtility.DisplayDialog(
                "Overwrite the original?",
                "This texture's file will be overwritten with the stretchable centre cut down to " +
                $"{settings.centerSize}px.\n\n{backup}\n\nTurn Overwrite Original off to write a new file " +
                "and keep the original untouched.",
                "Overwrite", "Cancel");
        }
    }
}
