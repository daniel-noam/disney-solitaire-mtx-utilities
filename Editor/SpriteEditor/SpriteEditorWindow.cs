using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// One texture, one preview, several tools that operate on it:
    ///
    /// <list type="bullet">
    /// <item><b>9-Slice</b> - auto-detect the stretchable band, correct the border by dragging the
    /// guides, check it against a stretch test, then write it, optionally cutting the redundant
    /// centre pixels out of the image.</item>
    /// <item><b>Mask</b> - flatten the sprite to a single colour, keeping its silhouette, optionally
    /// grown into a glow plate or reduced to an outline, and export it as a new file.</item>
    /// <item><b>Cleanup</b> - bleed colour out behind the transparent edge, crop the empty margin,
    /// pad the canvas to a power of two, moving the pivot and border to match.</item>
    /// </list>
    ///
    /// The image can be a project asset or any .png/.jpg on disk; a file from outside the project
    /// gives up only what lives in import settings, which is the 9-slice border.
    ///
    /// The window owns everything the tools share - the target, its pixels, zoom and pan - so adding
    /// a tool means writing a <see cref="SpriteEditorTool"/> and a field for it, nothing else.
    /// </summary>
    public class SpriteEditorWindow : EditorWindow
    {
        private const float PreviewMinHeight = 150f;
        private const float MinZoom = 0.05f;
        private const float MaxZoom = 32f;
        private const int CheckerSize = 16;

        /// <summary>Width of the settings column. Fixed, so the preview grows with the window.</summary>
        public const float OptionsPanelWidth = 320f;

        /// <summary>
        /// Height of the one-line hint under the preview. Pinned rather than left to the style so
        /// the settings column can mirror it as bottom padding and keep the action buttons level
        /// with the row above it - the hint is empty for some tools, and an empty label does not
        /// measure the same.
        /// </summary>
        public const float HintRowHeight = 15f;

        /// <summary>The readout plate in the corner of the picture.</summary>
        private const float OverlayMargin = 6f;
        private const float OverlayPadding = 5f;
        private const float OverlayHeight = HintRowHeight * 2f + OverlayPadding * 2f;

        /// <summary>
        /// The keys and gestures the window itself owns. Every tool shows this same line.
        /// </summary>
        private const string PreviewHint =
            "Scroll to zoom  ·  Alt or middle drag to pan  ·  F fits, 1 is 100%  ·  " +
            "A shows alpha, B cycles the backdrop";

        /// <summary>
        /// Wide enough that the 9-slice border row - label, four fields and both buttons - fits the
        /// preview column beside the settings panel.
        /// </summary>
        private static readonly Vector2 MinWindowSize = new Vector2(820, 540);

        /// <summary>What sits behind the image. White art on a grey checker is unreadable.</summary>
        public enum Backdrop
        {
            Checker = 0,
            Dark = 1,
            Light = 2,
        }

        /// <summary>
        /// Which channels the preview shows. Anything but RGB is drawn as opaque grey-scale, which
        /// is the only way to actually read an alpha channel rather than infer it from the checker.
        /// </summary>
        public enum PreviewChannel
        {
            RGB = 0,
            R = 1,
            G = 2,
            B = 3,
            A = 4,
        }

        /// <summary>Display names for <see cref="PreviewChannel"/>, indexed by its values.</summary>
        private static readonly string[] ChannelLabels = { "RGBA", "R", "G", "B", "A" };

        [SerializeField] private SpriteTarget target;
        [SerializeField] private SpriteEditorToolId activeTool = SpriteEditorToolId.NineSlice;
        [SerializeField] private NineSliceTool nineSliceTool = new NineSliceTool();
        [SerializeField] private MaskTool maskTool = new MaskTool();
        [SerializeField] private CleanupTool cleanupTool = new CleanupTool();
        [SerializeField] private Backdrop backdrop = Backdrop.Checker;
        [SerializeField] private PreviewChannel channel = PreviewChannel.RGB;
        [SerializeField] private bool fitToWindow = true;
        [SerializeField] private float zoom = 1f;
        [SerializeField] private Vector2 pan;

        /// <summary>Where the file browser opens next time. Convenience only.</summary>
        [SerializeField] private string lastBrowsedFolder = string.Empty;

        private Vector2 optionsScroll;

        // Frozen at Layout so the boxes below can be omitted when empty: whether a control exists
        // must not depend on a value that an edit in this same pass could change.
        private string frameWarning = "";
        private string frameSummary = "";
        private bool panning;

        /// <summary>A droppable texture is currently hovering the preview, so highlight it.</summary>
        private bool dropHover;

        // Rebuilt on demand: a domain reload nulls these without running OnEnable's initialisation
        // order, so they are checked for null rather than guarded by a bool.
        private SpriteSnapshot snapshot;

        /// <summary>Path whose pixels could not be read, so OnGUI stops retrying it every repaint.</summary>
        private string unreadablePath;

        private Texture2D checkerTexture;
        private GUIStyle overlayStyle;
        private GUIStyle previewBackgroundStyle;
        private GUIStyle centeredHintStyle;

        // Grey-scale of one channel of whatever was last drawn, rebuilt when the source texture,
        // the channel or the tint changes. Only ever one, because only one image is on screen.
        private Texture2D channelTexture;
        private int channelSourceId;
        private PreviewChannel channelBuiltFor;
        private Color channelTint;

        public static Color GuideColor => new Color(0.15f, 0.75f, 1f, 0.9f);
        public static Color GuideHotColor => new Color(1f, 0.72f, 0.1f, 1f);
        public static Color CenterFillColor => new Color(0.15f, 0.75f, 1f, 0.10f);
        public static Color BoundsColor => new Color(1f, 1f, 1f, 0.25f);

        /// <summary>The texture every tool works on. Null until one is picked.</summary>
        public SpriteTarget Target => target;

        /// <summary>Its pixels. Null when the file could not be decoded.</summary>
        public SpriteSnapshot Snapshot => snapshot;

        [MenuItem("Utilities/Sprite Editor", false, 1002)]
        public static void ShowWindow()
        {
            GetWindow<SpriteEditorWindow>("Sprite Editor").minSize = MinWindowSize;
        }

        /// <summary>Opens the window on a specific texture, optionally on a specific tool.</summary>
        public static SpriteEditorWindow ShowWith(string assetPath, SpriteEditorToolId? tool = null)
        {
            var window = GetWindow<SpriteEditorWindow>("Sprite Editor");
            window.minSize = MinWindowSize;
            if (tool.HasValue) window.activeTool = tool.Value;

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (asset != null) window.SetTarget(asset);
            return window;
        }

        private void OnEnable()
        {
            // Unity does not send MouseMove events unless a window asks for them, and without them
            // OnGUI's MouseMove repaint never fires — so hover states only update when something
            // else happens to trigger a frame, a tenth of a second behind the pointer.
            wantsMouseMove = true;

            AttachTools();
        }

        private void OnDisable()
        {
            if (tools != null)
                foreach (var tool in tools) tool.OnDisable();
            ReleaseSnapshot();
            ReleaseChannelTexture();
            if (checkerTexture != null)
            {
                DestroyImmediate(checkerTexture);
                checkerTexture = null;
            }
        }

        private void OnLostFocus()
        {
            if (tools != null)
                foreach (var tool in tools) tool.FlushPreferences();
        }

        /// <summary>
        /// Tab order, and the only place a tool is registered. The instances themselves are
        /// serialized fields, so their settings survive a domain reload; this array and the labels
        /// beside it are rebuilt after one, because OnGUI would otherwise allocate both every frame.
        /// </summary>
        [NonSerialized] private SpriteEditorTool[] tools;

        [NonSerialized] private string[] toolNames;

        private SpriteEditorTool ActiveTool
        {
            get
            {
                switch (activeTool)
                {
                    case SpriteEditorToolId.Mask: return maskTool;
                    case SpriteEditorToolId.Cleanup: return cleanupTool;
                    default: return nineSliceTool;
                }
            }
        }

        private void AttachTools()
        {
            if (tools != null) return;

            // Field initialisers do not run on a deserialized window, so a tool that was null before
            // the reload has to be rebuilt here.
            if (nineSliceTool == null) nineSliceTool = new NineSliceTool();
            if (maskTool == null) maskTool = new MaskTool();
            if (cleanupTool == null) cleanupTool = new CleanupTool();

            tools = new SpriteEditorTool[] { nineSliceTool, maskTool, cleanupTool };
            toolNames = new string[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                tools[i].Attach(this);
                toolNames[i] = tools[i].DisplayName;
            }
        }

        /// <summary>
        /// Runs work on the next editor tick, outside any OnGUI pass. Needed for two reasons:
        ///
        /// <list type="bullet">
        /// <item>IMGUI caches a group's control count during the Layout event and hands out those
        /// slots on every following event, so growing the set of controls part-way through a frame
        /// throws "Getting control N's position in a group with only M controls".</item>
        /// <item>EditorUtility.DisplayDialog refuses to open while a view is drawing - "This should
        /// not be called when a View's DrawRect Method is in progress". The top of OnGUI counts as
        /// drawing, even on the Layout event, so running it there is not enough.</item>
        /// </list>
        /// </summary>
        public void Defer(Action action)
        {
            var window = this;
            EditorApplication.delayCall += () =>
            {
                // The window can be closed between the click and the tick.
                if (window == null) return;
                action();
                window.Repaint();
            };
        }

        // -----------------------------------------------------------------------------------------
        // Layout
        // -----------------------------------------------------------------------------------------

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            AttachTools();
            EnsureResources();
            HandleShortcuts();

            // The whole layout is drawn whether or not a texture is loaded, so the window keeps its
            // shape instead of collapsing to a single card and rebuilding itself when one arrives.
            //
            // Stable for the pass: a texture only changes through Defer, which runs outside the GUI,
            // so this cannot flip between Layout and Repaint and change the control count.
            bool loaded = target != null;

            if (loaded && snapshot == null && unreadablePath != target.DisplayPath) LoadSnapshot();

            var tool = ActiveTool;

            if (Event.current.type == EventType.Layout)
            {
                frameWarning = loaded ? tool.GetWarning() : null;
                frameSummary = loaded ? tool.Summary : null;
            }

            // One margin of backdrop around everything and between the columns, so the panels read
            // as panels rather than as slabs pushed up against the window frame.
            GUILayout.Space(ToolStyles.SpaceL);
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            GUILayout.Space(ToolStyles.SpaceL);

            // Left: the picture and whatever the tool draws on it.
            EditorGUILayout.BeginVertical(ToolStyles.Card, GUILayout.ExpandHeight(true));
            DrawPreviewToolbar(tool);
            ToolStyles.Divider(ToolStyles.SpaceS);
            DrawPreviewSurface(tool);
            ToolStyles.Divider(ToolStyles.SpaceS);
            // One line, the same in every mode: these are the window's keys, and a key that moves
            // depending on which tool is open is a key nobody learns. Anything a single tool binds
            // is documented on the control it moves.
            //
            // Two lines' worth of minimum, not one. The preview above expands and takes whatever is
            // left, so a hint that wraps only gets the height this reserves for it — a one-line
            // minimum is exactly what was clipping the second line.
            GUILayout.Label(PreviewHint, ToolStyles.Hint, GUILayout.MinHeight(HintRowHeight * 2f));
            if (loaded) tool.DrawBelowPreview();
            EditorGUILayout.EndVertical();

            GUILayout.Space(ToolStyles.SpaceL);

            // Right: what is being edited, the tool's settings, and the buttons that commit them.
            EditorGUILayout.BeginVertical(GUILayout.Width(OptionsPanelWidth),
                GUILayout.ExpandHeight(true));

            // Pinned above the scroll view rather than inside it: the picker is the one control that
            // must never scroll out of reach, whatever the tool below it is showing.
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Texture");
                GUILayout.Space(ToolStyles.SpaceS);
                DrawTexturePicker();
            }

            GUILayout.Space(ToolStyles.SpaceM);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card, GUILayout.ExpandHeight(true)))
            {
                // The mode buttons are this panel's header. Which mode is selected already names
                // what the panel is showing, so a title above them would only say it twice.
                DrawToolModes();
                GUILayout.Space(ToolStyles.SpaceM);

                // A scope disposes while an exception unwinds; Begin/EndScrollView does not, so one
                // bad frame would otherwise break this window's clip stack for every frame after it.
                using (var options = new EditorGUILayout.ScrollViewScope(optionsScroll,
                           GUILayout.ExpandHeight(true)))
                {
                    optionsScroll = options.scrollPosition;
                    float previousLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 118f;

                    // The tools are only asked to draw once there is something to draw about: their
                    // settings and summaries read the texture, and would have to be null-guarded
                    // one by one otherwise.
                    if (loaded) tool.DrawOptions();
                    else GUILayout.Label("Load a texture to edit it.", ToolStyles.Placeholder);

                    EditorGUIUtility.labelWidth = previousLabelWidth;
                }

                // Last in the panel, with the scroll above it expanding — so the summary and the
                // committing buttons sit on the panel's floor however little the tool has to
                // configure. A space stands in for an empty summary so the control count holds.
                GUILayout.Space(ToolStyles.SpaceM);

                // Both boxes live with the button they describe rather than over by the picture: the
                // caveat first, then what pressing Apply will actually do, then Apply.
                //
                // With nothing loaded there is nothing to caveat and nothing to describe, so neither
                // is drawn — two empty boxes explaining nothing is worse than a shorter panel. Inside
                // the loaded state a blank still stands in for an empty one, so a warning appearing
                // or clearing cannot change the control count part-way through a frame; `loaded`
                // itself only ever changes between frames.
                if (!string.IsNullOrWhiteSpace(frameWarning))
                    EditorGUILayout.HelpBox(frameWarning, MessageType.Warning);

                if (!string.IsNullOrWhiteSpace(frameSummary))
                    EditorGUILayout.HelpBox(frameSummary, MessageType.Info);

                GUILayout.Space(ToolStyles.SpaceS);

                using (new ToolStyles.DisabledScope(!loaded))
                {
                    if (loaded) tool.DrawActions();
                    else DrawPlaceholderAction();
                }
            }

            EditorGUILayout.EndVertical();

            GUILayout.Space(ToolStyles.SpaceL);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(ToolStyles.SpaceL);
        }

        /// <summary>
        /// Which tool is showing, drawn as the header row of the settings panel.
        ///
        /// The three are modes of one editor, not tabs of one window, and the selected one already
        /// names what the panel below it contains — so they are the heading rather than sitting
        /// under one.
        /// </summary>
        private void DrawToolModes()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < toolNames.Length; i++)
                {
                    var id = (SpriteEditorToolId) i;
                    bool active = activeTool == id;

                    // Only the style differs between the active tool and the others, so the control
                    // count is the same whichever is selected.
                    if (!GUILayout.Button(toolNames[i], active ? ToolStyles.Primary : ToolStyles.Secondary,
                            GUILayout.Height(ToolStyles.ControlHeight))) continue;

                    // Tools put a different number of controls in the rows below, so the switch is
                    // deferred rather than taking effect half way through a frame.
                    if (!active) Defer(() => activeTool = id);
                }
            }
        }

        /// <summary>
        /// The picker, at the top of the settings column so it reads as what everything under it
        /// applies to. Drawn by the window, not by a tool: every tool works on the same texture.
        /// </summary>
        /// <summary>
        /// The texture, as a drop target with the object field beneath it.
        ///
        /// Drawn by the window, not by a tool: every tool works on the same texture. The Open File
        /// button is gone — dropping a file in from Finder already reaches the same code, so the
        /// button was a second door to the same room.
        /// </summary>
        private void DrawTexturePicker()
        {
            var rect = GUILayoutUtility.GetRect(0, ToolStyles.DropZoneHeight, GUILayout.ExpandWidth(true));
            bool hovering = HandleTextureDrop(rect);

            EditorGUI.DrawRect(rect, hovering
                ? ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.Accent, 0.25f)
                : ToolStyles.InsetBg);
            ToolStyles.DashedBorder(rect, hovering ? ToolStyles.Accent : ToolStyles.Faint,
                5f, 4f, hovering ? 2f : 1f);

            const float lineOne = 18f;
            const float lineTwo = 16f;
            float top = rect.y + (rect.height - (lineOne + lineTwo)) / 2f;
            var upper = new Rect(rect.x + 10, top, rect.width - 20, lineOne);
            var lower = new Rect(rect.x + 10, top + lineOne, rect.width - 20, lineTwo);

            if (target == null)
            {
                GUI.Label(upper, "Drag a texture here", ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(lower, "from the Project window or from Finder",
                    ToolStyles.Centred(ToolStyles.Hint));
            }
            else
            {
                GUI.Label(upper, target.FileName, ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(lower, ToolStyles.Elide(target.DisplayPath,
                    ToolStyles.MonoCharsFor(lower.width)), ToolStyles.Centred(ToolStyles.MonoSmall));
            }

            GUILayout.Space(ToolStyles.SpaceS);

            // labelWidth is a global editor setting, so it has to go back — leaving it at 56 would
            // squash the labels of every inspector drawn after this window.
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 56f;

            // Pinned to one line: given a free-height rect, EditorGUILayout draws the tall
            // thumbnail-preview variant of the field for any type with an object thumbnail, which
            // for a texture is a big square of art the preview beside it is already showing.
            var current = target?.asset;
            var picked = (Texture2D) EditorGUILayout.ObjectField("Texture", current, typeof(Texture2D), false,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            EditorGUIUtility.labelWidth = previousLabelWidth;

            // Swapping the texture changes how many controls the window draws, so it is deferred out
            // of the GUI pass.
            //
            // A texture with no asset path is refused rather than loaded: every tool here writes a
            // file beside the original, and there is no "beside" for something that only exists in
            // memory — a built-in texture, or one generated at runtime.
            if (picked == current) return;

            if (picked != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(picked)))
            {
                var refused = picked;
                Defer(() => Debug.LogWarning($"<b>[Sprite Editor]</b> '{refused.name}' is not a saved "
                    + "asset, so there is no file to read or write beside. Import it into the project first."));
                return;
            }

            Defer(() => SetTarget(picked));
        }

        /// <summary>
        /// Stands in for the tool's own buttons when nothing is loaded, so the panel keeps its
        /// floor and its height instead of the layout shifting the moment a texture arrives.
        /// </summary>
        private static void DrawPlaceholderAction()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Button("Apply", ToolStyles.Primary, GUILayout.Height(ToolStyles.ActionHeight));
            }
        }

        private void DrawPreviewToolbar(SpriteEditorTool tool)
        {
            // A plain row, not EditorStyles.toolbar: a full-bleed dark strip inside a rounded card
            // reads as something pasted in from another window.
            EditorGUILayout.BeginHorizontal();

            tool.DrawToolbar();

            GUILayout.FlexibleSpace();

            // Zoom sits with the channel and backdrop pickers, not with the tool's own controls:
            // Fit and 1:1 change how you are looking at the image, while a tool's toolbar chooses
            // what the image is. Same row, but the right-hand group is all "the view".
            DrawZoomControls();
            DrawChannelControls();
            DrawBackdropSwatch();

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Which channels the preview shows. The labels are written out rather than taken from the
        /// enum so the colour view reads as RGBA - it is the one that keeps the alpha, which is the
        /// whole distinction the menu is drawing.
        /// </summary>
        private void DrawChannelControls()
        {
            int pickedChannel = EditorGUILayout.Popup((int) channel, ChannelLabels,
                EditorStyles.toolbarDropDown, GUILayout.Width(58));
            if (pickedChannel != (int) channel)
            {
                channel = (PreviewChannel) pickedChannel;
                Repaint();
            }
        }

        /// <summary>
        /// The backdrop as a swatch of itself: three states, cycled by clicking, showing the thing
        /// they do. "Checker" written in a dropdown is a word you have to try before you know what
        /// it means; a square of checker is not.
        /// </summary>
        private void DrawBackdropSwatch()
        {
            if (GUILayout.Button(new GUIContent(string.Empty, $"Backdrop: {backdrop}  ·  B cycles"),
                    EditorStyles.toolbarButton, GUILayout.Width(28)))
            {
                backdrop = (Backdrop) (((int) backdrop + 1) % 3);
                Repaint();
            }

            // Layout hands out a placeholder rect, so the swatch is drawn on the pass that has a
            // real one.
            if (Event.current.type != EventType.Repaint) return;

            var rect = GUILayoutUtility.GetLastRect();
            var swatch = new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, rect.height - 8f);
            // A small cell: the preview's 16px checker fills a swatch this size with a single square,
            // which reads as flat grey - the one backdrop we do not have.
            DrawBackdrop(swatch, 5f);
            DrawOutline(swatch, ToolStyles.InsetBorder);
        }

        /// <summary>
        /// The picture's own numbers, in the corner of the picture - the scale reads as a property
        /// of what you are looking at rather than as another control in a row of controls.
        ///
        /// Drawn after the image, so under Fit the percentage is this frame's scale: the zoom is
        /// only settled once <see cref="ComputeImageRect"/> has run, and in the toolbar this showed
        /// the previous frame's number, visibly wrong for a frame on every resize.
        ///
        /// A dark plate under light text, because it has to stay legible over all three backdrops.
        /// These are GUI calls at fixed rects, so they take no part in the layout.
        /// </summary>
        private void DrawPreviewOverlay(Rect view)
        {
            if (Event.current.type != EventType.Repaint || snapshot == null) return;

            string size = $"{snapshot.Width} x {snapshot.Height}";
            if (!snapshot.IsSourceResolution) size += " (imported)";
            string percentage = $"{zoom * 100f:0}%";

            float width = Mathf.Max(overlayStyle.CalcSize(new GUIContent(size)).x,
                overlayStyle.CalcSize(new GUIContent(percentage)).x);
            var plate = new Rect(view.x + OverlayMargin, view.yMax - OverlayMargin - OverlayHeight,
                width + OverlayPadding * 2f, OverlayHeight);
            if (plate.width > view.width || plate.height > view.height) return;

            EditorGUI.DrawRect(plate, new Color(0f, 0f, 0f, 0.55f));

            var line = new Rect(plate.x + OverlayPadding, plate.y + OverlayPadding, width, HintRowHeight);
            GUI.Label(line, percentage, overlayStyle);
            line.y += HintRowHeight;
            GUI.Label(line, size, overlayStyle);
        }

        private void DrawPreviewSurface(SpriteEditorTool tool)
        {
            var rect = GUILayoutUtility.GetRect(10f, PreviewMinHeight,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            // Handled in window space, before the clipping group rebases coordinates.
            bool hovering = HandleTextureDrop(rect);

            GUI.Box(rect, GUIContent.none, previewBackgroundStyle);
            var view = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f);

            if (target == null)
            {
                GUI.Label(view, "No texture loaded", centeredHintStyle);
                return;
            }

            if (snapshot == null)
            {
                GUI.Label(view, "Could not read this texture's pixels.", centeredHintStyle);
                return;
            }

            // Grouping clips the contents and rebases coordinates on the view, so a panned or zoomed
            // image cannot bleed over the rest of the window.
            GUI.BeginGroup(view);
            tool.DrawPreview(new Rect(0f, 0f, view.width, view.height));
            GUI.EndGroup();

            DrawPreviewOverlay(view);

            // Drawn after the group so the highlight sits over the image, not under it.
            if (!hovering) return;
            EditorGUI.DrawRect(rect, new Color(ToolStyles.Accent.r, ToolStyles.Accent.g,
                ToolStyles.Accent.b, 0.15f));
            ToolStyles.DashedBorder(rect, ToolStyles.Accent, 6f, 5f, 2f);
            GUI.Label(rect, "Drop to edit this texture", ToolStyles.Centred(ToolStyles.CardTitle));
        }

        private void EnsureResources()
        {
            if (checkerTexture == null) checkerTexture = BuildCheckerTexture();

            if (previewBackgroundStyle == null) previewBackgroundStyle = ToolStyles.Inset;

            if (overlayStyle == null)
            {
                overlayStyle = new GUIStyle(EditorStyles.label) { fontSize = 10 };
                overlayStyle.normal.textColor = new Color(0.88f, 0.88f, 0.9f);
            }

            if (centeredHintStyle == null)
            {
                // Middle-centred: these labels are drawn into large rects, not layout rows.
                centeredHintStyle = new GUIStyle(ToolStyles.Placeholder)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter,
                };
            }
        }

        private static Texture2D BuildCheckerTexture()
        {
            var texture = new Texture2D(CheckerSize, CheckerSize, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point,
            };

            var dark = EditorGUIUtility.isProSkin ? new Color32(56, 56, 56, 255) : new Color32(168, 168, 168, 255);
            var light = EditorGUIUtility.isProSkin ? new Color32(74, 74, 74, 255) : new Color32(198, 198, 198, 255);

            var pixels = new Color32[CheckerSize * CheckerSize];
            int half = CheckerSize / 2;
            for (int y = 0; y < CheckerSize; y++)
            for (int x = 0; x < CheckerSize; x++)
                pixels[y * CheckerSize + x] = x < half == y < half ? dark : light;

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        // -----------------------------------------------------------------------------------------
        // Drop target
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Lets a texture be dragged straight onto <paramref name="area"/>. Returns true while a
        /// droppable one is hovering, so the caller can highlight the target.
        /// </summary>
        private bool HandleTextureDrop(Rect area)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.DragUpdated:
                {
                    bool inside = area.Contains(e.mousePosition);
                    bool droppable = inside && (DraggedTexture() != null || DraggedFile() != null);
                    if (inside)
                    {
                        DragAndDrop.visualMode = droppable
                            ? DragAndDropVisualMode.Copy
                            : DragAndDropVisualMode.Rejected;
                        e.Use();
                    }

                    SetDropHover(droppable);
                    break;
                }

                case EventType.DragPerform:
                {
                    // Cleared even for a drop elsewhere in the window, or the highlight sticks until
                    // the next drag.
                    SetDropHover(false);
                    if (!area.Contains(e.mousePosition)) break;

                    var picked = DraggedTexture();
                    string file = picked == null ? DraggedFile() : null;
                    if (picked != null || file != null)
                    {
                        DragAndDrop.AcceptDrag();

                        // Swapping the texture changes the window's control count, so it waits for
                        // the next Layout pass.
                        if (picked != null) Defer(() => SetTarget(picked));
                        else Defer(() => OpenFile(file));
                    }

                    e.Use();
                    break;
                }

                // Fires when the drag leaves the window entirely or is cancelled.
                case EventType.DragExited:
                    SetDropHover(false);
                    break;
            }

            return dropHover;
        }

        private void SetDropHover(bool hovering)
        {
            if (dropHover == hovering) return;
            dropHover = hovering;
            Repaint();
        }

        /// <summary>
        /// First texture in the drag. A Sprite is resolved to the texture behind it, so dragging
        /// either the image asset or one of its sprites works.
        /// </summary>
        private static Texture2D DraggedTexture()
        {
            foreach (var dragged in DragAndDrop.objectReferences)
            {
                var texture = dragged as Texture2D;
                if (texture == null && dragged is Sprite sprite) texture = sprite.texture;
                if (texture != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                    return texture;
            }

            return null;
        }

        /// <summary>
        /// First decodable image file in the drag. This is how a drop from Finder arrives - it
        /// carries paths rather than objects, and Unity knows nothing about the file.
        /// </summary>
        private static string DraggedFile()
        {
            foreach (string path in DragAndDrop.paths)
                if (!string.IsNullOrEmpty(path) && SpriteImage.IsRewritableImage(path)) return path;

            return null;
        }

        // -----------------------------------------------------------------------------------------
        // Shared preview services
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Keys the whole window answers to. The active tool gets first refusal, so a tool can bind
        /// something the window would otherwise swallow.
        ///
        /// Skipped while a text field has focus - otherwise typing "f" in the suffix field would
        /// refit the preview instead of writing an f.
        /// </summary>
        private void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;
            if (target == null || snapshot == null) return;

            if (ActiveTool.HandleShortcut(e))
            {
                e.Use();
                Repaint();
                return;
            }

            switch (e.keyCode)
            {
                case KeyCode.F:
                    fitToWindow = true;
                    break;

                case KeyCode.Alpha1:
                case KeyCode.Keypad1:
                    fitToWindow = false;
                    zoom = 1f;
                    pan = Vector2.zero;
                    break;

                case KeyCode.Alpha0:
                case KeyCode.Keypad0:
                    channel = PreviewChannel.RGB;
                    break;

                case KeyCode.A:
                    // The one channel worth a key of its own: it is what a mask actually is.
                    channel = channel == PreviewChannel.A ? PreviewChannel.RGB : PreviewChannel.A;
                    break;

                case KeyCode.B:
                    backdrop = (Backdrop) (((int) backdrop + 1) % 3);
                    break;

                default:
                    return;
            }

            e.Use();
            Repaint();
        }

        /// <summary>Fit and 1:1, in the view group of the preview toolbar. The percentage they set
        /// is read off the status strip under the image.</summary>
        private void DrawZoomControls()
        {
            fitToWindow = GUILayout.Toggle(fitToWindow, "Fit", EditorStyles.toolbarButton, GUILayout.Width(34));

            if (!GUILayout.Button("1:1", EditorStyles.toolbarButton, GUILayout.Width(34))) return;
            fitToWindow = false;
            zoom = 1f;
            pan = Vector2.zero;
        }

        /// <summary>
        /// Where an image of this size lands inside the view, honouring Fit, zoom and pan.
        /// <paramref name="scale"/> is screen pixels per texture pixel.
        /// </summary>
        public Rect ComputeImageRect(Rect view, int width, int height, out float scale)
        {
            float fit = Mathf.Min(view.width / Mathf.Max(1, width), view.height / Mathf.Max(1, height));
            if (fitToWindow)
            {
                // Keep the zoom readout honest and make leaving Fit continue from where we are.
                zoom = fit;
                pan = Vector2.zero;
            }

            scale = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            var size = new Vector2(width * scale, height * scale);
            var center = view.center + pan;
            return new Rect(center.x - size.x * 0.5f, center.y - size.y * 0.5f, size.x, size.y);
        }

        /// <summary>
        /// Alt/middle-drag panning and wheel zoom. Returns true when it consumed the event, so a
        /// tool can call it first and only handle what is left.
        /// </summary>
        public bool HandleNavigation(Rect view, Rect imageRect, float scale)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!view.Contains(e.mousePosition)) return false;
                    if (e.button != 2 && !(e.button == 0 && e.alt)) return false;
                    panning = true;
                    fitToWindow = false;
                    e.Use();
                    return true;

                case EventType.MouseDrag:
                    if (!panning) return false;
                    pan += e.delta;
                    e.Use();
                    Repaint();
                    return true;

                case EventType.MouseUp:
                    if (!panning) return false;
                    panning = false;
                    e.Use();
                    Repaint();
                    return true;

                case EventType.ScrollWheel:
                    if (!view.Contains(e.mousePosition)) return false;
                    ZoomAt(e.mousePosition, view, imageRect, scale, zoom * (1f - e.delta.y * 0.05f));
                    e.Use();
                    Repaint();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Zooms while keeping the texture pixel under the cursor in place.</summary>
        private void ZoomAt(Vector2 mouse, Rect view, Rect imageRect, float scale, float newZoom)
        {
            var texturePoint = (mouse - new Vector2(imageRect.x, imageRect.y)) / Mathf.Max(0.0001f, scale);
            var imageSize = new Vector2(imageRect.width, imageRect.height) / Mathf.Max(0.0001f, scale);

            fitToWindow = false;
            zoom = Mathf.Clamp(newZoom, MinZoom, MaxZoom);
            pan = mouse + imageSize * zoom * 0.5f - view.center - texturePoint * zoom;
        }

        /// <summary>
        /// Draws a tool's image through the channel filter. <paramref name="tint"/> multiplies it -
        /// the mask tool builds its preview white and colours it here - and is folded into the
        /// grey-scale for a single-channel view, so what you read is what gets written.
        /// </summary>
        public void DrawImage(Rect rect, Texture2D texture, Color tint, FilterMode filter)
        {
            if (texture == null) return;

            texture.filterMode = filter;
            var resolved = ResolveDisplayTexture(texture, tint, out bool alphaBlend);

            var previousColor = GUI.color;

            // A channel view has the tint baked in already, and is opaque by definition.
            if (alphaBlend) GUI.color = tint;
            GUI.DrawTexture(rect, resolved, ScaleMode.StretchToFill, alphaBlend);
            GUI.color = previousColor;
        }

        /// <summary>
        /// The texture to actually draw for <paramref name="source"/>: itself in RGB, or a cached
        /// grey-scale of one channel. For callers that cannot use <see cref="DrawImage"/> because
        /// they draw the texture in pieces.
        /// </summary>
        public Texture2D ResolveDisplayTexture(Texture2D source, Color tint, out bool alphaBlend)
        {
            alphaBlend = channel == PreviewChannel.RGB;
            if (source == null || alphaBlend) return source;

            int id = source.GetInstanceID();
            if (channelTexture == null || channelSourceId != id ||
                channelBuiltFor != channel || channelTint != tint)
            {
                ReleaseChannelTexture();
                channelTexture = BuildChannelTexture(source, tint, channel);
                if (channelTexture == null)
                {
                    // Unreadable source - fall back to drawing it as it is rather than nothing.
                    alphaBlend = true;
                    return source;
                }

                channelSourceId = id;
                channelBuiltFor = channel;
                channelTint = tint;
            }

            channelTexture.filterMode = source.filterMode;
            return channelTexture;
        }

        private static Texture2D BuildChannelTexture(Texture2D source, Color tint, PreviewChannel channel)
        {
            Color32[] pixels;
            try
            {
                pixels = source.GetPixels32();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{SpriteImage.Log} Could not read '{source.name}' to isolate a " +
                                 $"channel: {exception.Message}");
                return null;
            }

            var multiplier = (Color32) tint;
            var output = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                byte value;
                switch (channel)
                {
                    case PreviewChannel.R: value = (byte) (pixel.r * multiplier.r / 255); break;
                    case PreviewChannel.G: value = (byte) (pixel.g * multiplier.g / 255); break;
                    case PreviewChannel.B: value = (byte) (pixel.b * multiplier.b / 255); break;
                    default: value = (byte) (pixel.a * multiplier.a / 255); break;
                }

                output[i] = new Color32(value, value, value, 255);
            }

            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };

            texture.SetPixels32(output);
            texture.Apply(false);
            return texture;
        }

        private void ReleaseChannelTexture()
        {
            if (channelTexture != null) DestroyImmediate(channelTexture);
            channelTexture = null;
            channelSourceId = 0;
        }

        /// <summary>Whatever the user picked to sit behind the image.</summary>
        public void DrawBackdrop(Rect rect) => DrawBackdrop(rect, CheckerSize);

        /// <summary><paramref name="cell"/> is the checker's square in screen pixels.</summary>
        private void DrawBackdrop(Rect rect, float cell)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;

            switch (backdrop)
            {
                case Backdrop.Dark:
                    EditorGUI.DrawRect(rect, new Color(0.06f, 0.06f, 0.07f, 1f));
                    break;
                case Backdrop.Light:
                    EditorGUI.DrawRect(rect, new Color(0.88f, 0.88f, 0.9f, 1f));
                    break;
                default:
                    GUI.DrawTextureWithTexCoords(rect, checkerTexture,
                        new Rect(0f, 0f, rect.width / cell, rect.height / cell), false);
                    break;
            }
        }

        public static void DrawOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        // -----------------------------------------------------------------------------------------
        // Target
        // -----------------------------------------------------------------------------------------

        public void SetTarget(Texture2D asset)
        {
            if (asset == null)
            {
                SetTarget((SpriteTarget) null);
                return;
            }

            var next = SpriteTarget.FromAsset(asset);
            if (next == null)
            {
                Debug.LogWarning($"{SpriteImage.Log} '{asset.name}' is not saved as an asset, so there is " +
                                 "nothing on disk to edit or export from.");
                Repaint();
                return;
            }

            SetTarget(next);
        }

        /// <summary>Loads an image file from anywhere on disk, project or not.</summary>
        public void OpenFile(string path)
        {
            var next = SpriteTarget.FromFile(path);
            if (next != null) SetTarget(next);
        }

        public void SetTarget(SpriteTarget next)
        {
            // Defers are queued per OnGUI pass, so the same swap can be scheduled twice; re-decoding
            // the same image would just be wasted work.
            if (next != null && target != null && target.DisplayPath == next.DisplayPath && snapshot != null)
                return;

            ReleaseSnapshot();
            unreadablePath = null;
            fitToWindow = true;
            pan = Vector2.zero;

            target = next;
            if (target == null)
            {
                Repaint();
                return;
            }

            RefreshTargetInfo();
            LoadSnapshot();
            AttachTools();
            foreach (var tool in tools) tool.OnTargetChanged();

            Repaint();
        }

        /// <summary>Asks for a file outside the project. Never call this during a GUI pass.</summary>
        private void BrowseForFile()
        {
            string picked = EditorUtility.OpenFilePanelWithFilters(
                "Open Image", lastBrowsedFolder, new[] { "Images", "png,jpg,jpeg" });

            if (string.IsNullOrEmpty(picked)) return;

            lastBrowsedFolder = Path.GetDirectoryName(picked) ?? string.Empty;
            OpenFile(picked);
        }

        /// <summary>Re-reads the current file, for edits made outside Unity or by a tool.</summary>
        public void ReloadTarget()
        {
            if (target == null) return;
            unreadablePath = null;
            RefreshTargetInfo();
            LoadSnapshot();
            AttachTools();
            foreach (var tool in tools) tool.OnTargetChanged();
            Repaint();
        }

        /// <summary>
        /// Re-reads the importer without touching the pixels. Cheap, and needed after a tool writes
        /// import settings - otherwise a "this is not a Sprite" warning outlives the Apply that
        /// turned it into one.
        /// </summary>
        public void RefreshTargetInfo()
        {
            if (target == null || target.IsExternal) return;

            var importer = AssetImporter.GetAtPath(target.assetPath) as TextureImporter;
            var textureType = importer == null ? TextureImporterType.Default : importer.textureType;
            target.isSprite = textureType == TextureImporterType.Sprite;
            target.textureTypeName = textureType.ToString();
        }

        private void LoadSnapshot()
        {
            ReleaseSnapshot();

            // A file outside the project has no importer to fall back on, so it is decoded directly
            // or not at all.
            snapshot = target.IsExternal
                ? SpriteImage.LoadFile(target.absolutePath, out string error)
                : SpriteImage.Load(target.assetPath, out error);

            if (snapshot == null)
            {
                unreadablePath = target.DisplayPath;

                // The preview says "could not read this texture's pixels"; the Console carries why.
                Debug.LogWarning($"{SpriteImage.Log} {target.DisplayPath}: {error}");
                return;
            }

            unreadablePath = null;
        }

        private void ReleaseSnapshot()
        {
            snapshot?.Dispose();
            snapshot = null;
        }
    }
}
