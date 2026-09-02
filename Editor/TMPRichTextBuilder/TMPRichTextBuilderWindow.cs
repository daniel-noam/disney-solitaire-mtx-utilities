using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Editor window for composing TextMeshPro rich text with tag helpers,
    /// a full tag reference, and a color picker for &lt;color&gt; tags.
    /// </summary>
    public class TMPRichTextBuilderWindow : EditorWindow
    {
        private const string TextControlName = "TMPRichTextBuilder.Text";
        private const int MenuPriority = 1011;
        private const float SplitterWidth = 4f;
        private const float MinPanelWidth = 280f;
        private const int MaxUndoSteps = 64;

        [SerializeField] private string richText = "Hello <b>World</b>!";
        [SerializeField] private Color tagColor = new Color(1f, 0.35f, 0.2f, 1f);
        [SerializeField] private Color markColor = new Color(1f, 1f, 0f, 0.4f);
        [SerializeField] private float sizeValue = 36f;
        [SerializeField] private SizeMode sizeMode = SizeMode.Absolute;
        [SerializeField] private string fontName = "";
        [SerializeField] private string materialName = "";
        [SerializeField] private string styleName = "";
        [SerializeField] private string spriteName = "";
        [SerializeField] private int spriteIndex;
        [SerializeField] private string linkId = "id";
        [SerializeField] private string gradientName = "";
        [SerializeField] private float spacingValue = 0.5f;
        [SerializeField] private float vOffsetValue = 1f;
        [SerializeField] private float indentValue = 10f;
        [SerializeField] private float rotateValue = 45f;
        [SerializeField] private string alphaHex = "FF";
        [SerializeField] private bool wrapSelection = true;
        [SerializeField] private bool includeAlphaInColor = true;
        [SerializeField] private float leftPanelWidth = 420f;

        private Vector2 leftScroll;
        private Vector2 rightScroll;
        private GUIStyle textAreaStyle;
        private GUIStyle previewStyle;
        private GUIStyle tagButtonStyle;
        private GUIStyle categoryHeaderStyle;
        private bool stylesReady;
        private string filter = "";
        private bool isResizingSplitter;

        // Cached while the composer is focused — button clicks steal focus before insert runs.
        private int cachedCursor;
        private int cachedSelect;
        private bool hasCachedCaret;
        private bool forceReleaseTextFocus;
        private bool pendingCaretRestore;

        private static FieldInfo recycledEditorField;
        private static bool recycledEditorFieldSearched;

        // Refreshed on selection change instead of walking the selection on every repaint - the Apply button
        // needs the count every frame just to label itself.
        private readonly List<TMP_Text> selectedTmps = new List<TMP_Text>();

        private readonly List<TextState> undoStack = new List<TextState>();
        private readonly List<TextState> redoStack = new List<TextState>();
        private bool suppressTextTracking;
        private bool typingGroupOpen;
        private bool composerWasFocused;

        private static readonly RichTag[] AllTags =
        {
            new RichTag("Bold", "Formatting", "<b>", "</b>", "Bold text"),
            new RichTag("Italic", "Formatting", "<i>", "</i>", "Italic text"),
            new RichTag("Underline", "Formatting", "<u>", "</u>", "Underline"),
            new RichTag("Strikethrough", "Formatting", "<s>", "</s>", "Strikethrough"),
            new RichTag("Subscript", "Formatting", "<sub>", "</sub>", "Subscript"),
            new RichTag("Superscript", "Formatting", "<sup>", "</sup>", "Superscript"),
            new RichTag("Mark", "Formatting", "<mark=#FFFF00AA>", "</mark>", "Highlight / mark"),
            new RichTag("No Break", "Formatting", "<nobr>", "</nobr>", "Prevent line break"),
            new RichTag("No Parse", "Formatting", "<noparse>", "</noparse>", "Show tags as literal text"),

            new RichTag("Uppercase", "Case", "<uppercase>", "</uppercase>", "Force uppercase"),
            new RichTag("Lowercase", "Case", "<lowercase>", "</lowercase>", "Force lowercase"),
            new RichTag("Smallcaps", "Case", "<smallcaps>", "</smallcaps>", "Small capitals"),
            new RichTag("All Caps", "Case", "<allcaps>", "</allcaps>", "Alias for uppercase"),

            new RichTag("Color (hex)", "Color", "<color=#FF0000>", "</color>", "Hex color #RRGGBB or #RRGGBBAA"),
            new RichTag("Color shorthand", "Color", "<#FF0000>", "</color>", "Shorthand hex color"),
            new RichTag("Color red", "Color", "<color=red>", "</color>", "Named color"),
            new RichTag("Color green", "Color", "<color=green>", "</color>", "Named color"),
            new RichTag("Color blue", "Color", "<color=blue>", "</color>", "Named color"),
            new RichTag("Color black", "Color", "<color=black>", "</color>", "Named color"),
            new RichTag("Color white", "Color", "<color=white>", "</color>", "Named color"),
            new RichTag("Color yellow", "Color", "<color=yellow>", "</color>", "Named color"),
            new RichTag("Color orange", "Color", "<color=orange>", "</color>", "Named color"),
            new RichTag("Color purple", "Color", "<color=purple>", "</color>", "Named color"),
            new RichTag("Alpha", "Color", "<alpha=#FF>", "", "Set opacity for following text (#00–#FF)"),

            new RichTag("Size", "Size / Font", "<size=36>", "</size>", "Absolute or relative size (+2, 80%)"),
            new RichTag("Font", "Size / Font", "<font=\"Font Name\">", "</font>", "Switch font asset by name"),
            new RichTag("Material", "Size / Font", "<material=\"Material Name\">", "</material>", "Switch material preset"),
            new RichTag("Style", "Size / Font", "<style=\"Style Name\">", "</style>", "Apply TMP style sheet entry"),
            new RichTag("Gradient", "Size / Font", "<gradient=\"Gradient Name\">", "</gradient>", "Apply color gradient preset"),

            new RichTag("Character Space", "Spacing", "<cspace=0.5>", "</cspace>", "Character spacing"),
            new RichTag("Mono Space", "Spacing", "<mspace=0.5>", "</mspace>", "Monospaced characters"),
            new RichTag("Space", "Spacing", "<space=5>", "", "Insert horizontal space"),
            new RichTag("Vertical Offset", "Spacing", "<voffset=1em>", "</voffset>", "Vertical offset"),
            new RichTag("Line Height", "Spacing", "<line-height=100%>", "</line-height>", "Line height"),
            new RichTag("Indent", "Spacing", "<indent=15%>", "</indent>", "Indent paragraph"),
            new RichTag("Margin", "Spacing", "<margin=5em>", "</margin>", "Left/right margin"),
            new RichTag("Margin Left", "Spacing", "<margin-left=5em>", "", "Left margin only"),
            new RichTag("Margin Right", "Spacing", "<margin-right=5em>", "", "Right margin only"),
            new RichTag("Position", "Spacing", "<pos=75%>", "", "Absolute character position"),

            new RichTag("Align Left", "Alignment", "<align=\"left\">", "</align>", "Left align"),
            new RichTag("Align Center", "Alignment", "<align=\"center\">", "</align>", "Center align"),
            new RichTag("Align Right", "Alignment", "<align=\"right\">", "</align>", "Right align"),
            new RichTag("Align Justified", "Alignment", "<align=\"justified\">", "</align>", "Justify"),
            new RichTag("Align Flush", "Alignment", "<align=\"flush\">", "</align>", "Flush justify"),

            new RichTag("Sprite Index", "Sprites / Links", "<sprite=0>", "", "Inline sprite by index"),
            new RichTag("Sprite Name", "Sprites / Links", "<sprite name=\"Name\">", "", "Inline sprite by name"),
            new RichTag("Sprite Asset", "Sprites / Links", "<sprite=\"Asset\" index=0>", "", "Sprite from named asset"),
            new RichTag("Link", "Sprites / Links", "<link=\"id\">", "</link>", "Clickable link id"),
            new RichTag("Rotate", "Sprites / Links", "<rotate=45>", "</rotate>", "Rotate characters"),
            new RichTag("Line Break", "Sprites / Links", "<br>", "", "Forced line break"),
            new RichTag("Page Break", "Sprites / Links", "<page>", "", "Page break (overflow page mode)"),
        };

        [MenuItem("Utilities/TMP Rich Text Builder", false, MenuPriority)]
        public static void Open()
        {
            var win = GetWindow<TMPRichTextBuilderWindow>("TMP Rich Text Builder");
            win.minSize = new Vector2(820, 520);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("TMP Rich Text Builder");

            // Without this, hover states only repaint when something else triggers a frame.
            wantsMouseMove = true;
            typingGroupOpen = false;
            RefreshSelection();
        }

        private void OnFocus() => RefreshSelection();

        private void OnSelectionChange()
        {
            RefreshSelection();
            Repaint();
        }

        private void RefreshSelection()
        {
            selectedTmps.Clear();

            foreach (GameObject go in Selection.gameObjects)
            {
                if (!go) continue;

                foreach (TMP_Text tmp in go.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (tmp && !selectedTmps.Contains(tmp)) selectedTmps.Add(tmp);
                }
            }
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;

            // Monospaced: the composer's content is markup, and tags line up and read far better
            // in a fixed pitch than in a proportional face.
            textAreaStyle = new GUIStyle(ToolStyles.TextArea) { fontSize = 12 };

            previewStyle = new GUIStyle(ToolStyles.Inset)
            {
                richText = true,
                wordWrap = true,
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(12, 12, 16, 16)
            };
            previewStyle.normal.textColor = ToolStyles.Text;

            // A tag row is a button, so it is the ordinary button — just left-aligned, because it
            // is really a list entry you can press.
            tagButtonStyle = new GUIStyle(ToolStyles.SecondaryCompact)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = ToolStyles.ControlHeight,
                margin = new RectOffset(0, 0, 1, 1),
                padding = new RectOffset(8, 8, 0, 0)
            };

            categoryHeaderStyle = new GUIStyle(ToolStyles.ColumnHeader)
            {
                margin = new RectOffset(0, 0, 8, 2)
            };

            stylesReady = true;
        }

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            EnsureStyles();
            HandleUndoHotkeys();

            // Capture caret before any right-panel control can steal focus this event.
            TryCacheCaret();

            GUILayout.Space(ToolStyles.SpaceL);

            Rect body = EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            GUILayout.Space(ToolStyles.SpaceL);

            float maxLeft = Mathf.Max(MinPanelWidth, position.width - MinPanelWidth - SplitterWidth - 8f);
            leftPanelWidth = Mathf.Clamp(leftPanelWidth, MinPanelWidth, maxLeft);

            // Left: composer + preview
            EditorGUILayout.BeginVertical(GUILayout.Width(leftPanelWidth), GUILayout.ExpandHeight(true));
            leftScroll = EditorGUILayout.BeginScrollView(leftScroll);
            DrawLeftPanel();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            DrawSplitter(body.height > 1f ? body.height : position.height - 24f);

            // Right: tags + helpers
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            rightScroll = EditorGUILayout.BeginScrollView(rightScroll);
            DrawRightPanel();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            GUILayout.Space(ToolStyles.SpaceL);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            GUILayout.Space(ToolStyles.SpaceL);

            // Keep caching after the text field has drawn this frame.
            TryCacheCaret();

            bool composerFocused = GUI.GetNameOfFocusedControl() == TextControlName;
            if (composerWasFocused && !composerFocused)
                typingGroupOpen = false;
            composerWasFocused = composerFocused;
        }

        private void DrawSplitter(float height)
        {
            Rect splitterRect = GUILayoutUtility.GetRect(SplitterWidth, SplitterWidth, GUILayout.ExpandHeight(true));
            splitterRect.height = Mathf.Max(height, splitterRect.height);

            // Only the grip is drawn, centred in the gap. Filling the whole splitter put a third
            // coloured band between two panels that are already separated by the backdrop.
            var grip = new Rect(splitterRect.center.x - 1f, splitterRect.y + 8f, 2f,
                Mathf.Max(0f, splitterRect.height - 16f));
            EditorGUI.DrawRect(grip, ToolStyles.CardBorder);
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);

            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when splitterRect.Contains(e.mousePosition):
                    isResizingSplitter = true;
                    e.Use();
                    break;
                case EventType.MouseDrag when isResizingSplitter:
                    leftPanelWidth = Mathf.Clamp(leftPanelWidth + e.delta.x, MinPanelWidth, position.width - MinPanelWidth - SplitterWidth - 8f);
                    Repaint();
                    e.Use();
                    break;
                case EventType.MouseUp when isResizingSplitter:
                    isResizingSplitter = false;
                    e.Use();
                    break;
            }
        }

        /// <summary>
        /// Composer undo/redo on the keyboard.
        ///
        /// Reconstructed after the buttons were removed — the shortcuts are the ones their tooltips
        /// documented (Ctrl/Cmd+Z, Ctrl/Cmd+Shift+Z, Ctrl/Cmd+Y). Both operations already no-op on
        /// an empty stack, so this only decides when to consume the key.
        ///
        /// Guarded on the composer having focus: these are the editor's own undo shortcuts, and
        /// swallowing them while the user is working anywhere else in Unity would be worse than
        /// having no shortcut at all.
        /// </summary>
        private void HandleUndoHotkeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;
            if (!e.control && !e.command) return;
            if (GUI.GetNameOfFocusedControl() != TextControlName) return;

            if (e.keyCode == KeyCode.Z && e.shift)
            {
                PerformRedo();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Z)
            {
                PerformUndo();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Y)
            {
                PerformRedo();
                e.Use();
            }
        }

        private void DrawLeftPanel()
        {
            EditorGUILayout.BeginVertical(ToolStyles.Card);
            ToolStyles.CardHeader("Composer");
            GUILayout.Space(ToolStyles.SpaceM);

            if (forceReleaseTextFocus)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                EditorGUIUtility.editingTextField = false;
                forceReleaseTextFocus = false;
            }

            float textHeight = Mathf.Max(160f, position.height * 0.35f);
            GUI.SetNextControlName(TextControlName);
            EditorGUI.BeginChangeCheck();
            string edited = EditorGUILayout.TextArea(richText ?? "", textAreaStyle, GUILayout.MinHeight(textHeight));
            if (EditorGUI.EndChangeCheck())
                TrackTypedTextChange(edited);
            else
                richText = edited;

            if (pendingCaretRestore)
                ApplyCaretToActiveEditor();

            TryCacheCaret();

            GUILayout.Space(ToolStyles.SpaceS);
            DrawComposerActions();
            EditorGUILayout.EndVertical();

            GUILayout.Space(ToolStyles.SpaceL);

            EditorGUILayout.BeginVertical(ToolStyles.Card);
            ToolStyles.CardHeader("Preview");
            GUILayout.Space(ToolStyles.SpaceS);
            EditorGUILayout.LabelField(
                new GUIContent("Preview (approximate)",
                               "Rendered with Unity's IMGUI rich text, which supports only bold, italic, size " +
                               "and color. Other TMP tags are hidden here but still present in the text."),
                ToolStyles.ColumnHeader);

            string preview = ConvertToUnityPreview(richText);
            float previewWidth = Mathf.Max(50f, leftPanelWidth - 36f);
            float previewHeight = previewStyle.CalcHeight(new GUIContent(preview), previewWidth);
            previewHeight = Mathf.Clamp(previewHeight, 56f, 800f);
            GUILayout.Label(preview, previewStyle, GUILayout.Height(previewHeight), GUILayout.ExpandWidth(true));

            GUILayout.Label($"{(richText ?? "").Length} characters", ToolStyles.Hint);

            // What you do with the finished text belongs under the thing that shows you the
            // finished text, not under the box you were typing in.
            GUILayout.Space(ToolStyles.SpaceM);
            DrawBottomActions();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// The three controls that act on the composer, directly under it.
        ///
        /// They were in a window toolbar, which put them as far from the text they change as the
        /// layout allows. Undo and Redo are gone from here entirely — they duplicate the keyboard
        /// shortcuts that still work, and a text field is the one place nobody looks for an undo
        /// button.
        /// </summary>
        private void DrawComposerActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // The setting on the left, the two actions on the right — a checkbox that changes
                // how inserting behaves is not the same kind of thing as a button that does something.
                wrapSelection = EditorGUILayout.ToggleLeft(
                    new GUIContent("Wrap selection",
                        "On: wrap selected text, or insert open/close around the caret.\n"
                        + "Off: insert tags at the caret."),
                    wrapSelection, GUILayout.Width(ToolStyles.ButtonL));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Load from TMP", ToolStyles.Secondary,
                        GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ControlHeight)))
                    LoadFromSelectedTmp();

                // No confirmation: the composer has undo, so clearing it is not a decision that
                // needs defending with a dialog.
                if (GUILayout.Button("Clear", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                        GUILayout.Height(ToolStyles.ControlHeight)))
                    CommitTextChange("", 0, 0);
            }
        }

        private void DrawBottomActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string applyLabel = selectedTmps.Count > 1 ? $"Apply to TMP ({selectedTmps.Count})" : "Apply to TMP";
                string applyTooltip = selectedTmps.Count == 0
                    ? "No TMP selected"
                    : selectedTmps.Count == 1
                        ? $"Apply to {selectedTmps[0].name}"
                        : $"Apply to {selectedTmps.Count} selected TMP objects";

                using (new ToolStyles.DisabledScope(selectedTmps.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent(applyLabel, applyTooltip), ToolStyles.Primary,
                            GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ActionHeight)))
                        ApplyToSelectedTmps();
                }

                if (GUILayout.Button("Copy", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                        GUILayout.Height(ToolStyles.ActionHeight)))
                {
                    EditorGUIUtility.systemCopyBuffer = richText ?? "";
                    ShowNotification(new GUIContent("Copied to clipboard"));
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void DrawRightPanel()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card)) DrawColorSection();
            GUILayout.Space(ToolStyles.SpaceL);
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card)) DrawParameterizedInserts();
            GUILayout.Space(ToolStyles.SpaceL);
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card)) DrawTagReference();
        }

        private void DrawColorSection()
        {
            ToolStyles.CardHeader("Colour");
            GUILayout.Space(ToolStyles.SpaceM);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Inset))
            {
                tagColor = EditorGUILayout.ColorField(new GUIContent("Color"), tagColor);
                includeAlphaInColor = EditorGUILayout.ToggleLeft("Include alpha (#RRGGBBAA)", includeAlphaInColor);

                string hex = ColorToHex(tagColor, includeAlphaInColor);
                EditorGUILayout.LabelField("Hex", "#" + hex);

                if (GUILayout.Button($"Insert <color=#{hex}>", ToolStyles.Secondary,
                        GUILayout.Height(ToolStyles.ActionHeight)))
                    InsertOrWrap($"<color=#{hex}>", "</color>");

                if (GUILayout.Button($"Insert <#{hex}>", ToolStyles.Secondary,
                        GUILayout.Height(ToolStyles.ControlHeight)))
                    InsertOrWrap($"<#{hex}>", "</color>");

                EditorGUILayout.Space(4);
                GUILayout.Label("NAMED COLOURS", ToolStyles.ColumnHeader);
                DrawNamedColorRow(new[] { "red", "green", "blue", "black", "white", "yellow", "orange", "purple", "grey", "clear" });

                EditorGUILayout.Space(4);
                markColor = EditorGUILayout.ColorField(new GUIContent("Mark"), markColor);
                string markHex = ColorToHex(markColor, true);
                if (GUILayout.Button($"Insert <mark=#{markHex}>", ToolStyles.Secondary,
                        GUILayout.Height(ToolStyles.ControlHeight)))
                    InsertOrWrap($"<mark=#{markHex}>", "</mark>");

                using (new EditorGUILayout.HorizontalScope())
                {
                    alphaHex = EditorGUILayout.TextField("Alpha", alphaHex);
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertRaw($"<alpha=#{NormalizeAlphaHex(alphaHex)}>");
                }
            }
        }

        private void DrawNamedColorRow(string[] names)
        {
            const int perRow = 5;
            for (int i = 0; i < names.Length; i += perRow)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int j = i; j < Mathf.Min(i + perRow, names.Length); j++)
                    {
                        string name = names[j];
                        if (GUILayout.Button(name, ToolStyles.SecondaryCompact,
                            GUILayout.Height(ToolStyles.ControlHeight)))
                            InsertOrWrap($"<color={name}>", "</color>");
                    }
                }
            }
        }

        private void DrawParameterizedInserts()
        {
            ToolStyles.CardHeader("Parameterised tags");
            GUILayout.Space(ToolStyles.SpaceM);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Inset))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    sizeValue = EditorGUILayout.FloatField("Size", sizeValue);
                    sizeMode = (SizeMode)EditorGUILayout.EnumPopup(sizeMode, GUILayout.Width(90));
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                    {
                        string open = sizeMode switch
                        {
                            SizeMode.Plus => $"<size=+{F(sizeValue)}>",
                            SizeMode.Percent => $"<size={F(sizeValue)}%>",
                            _ => $"<size={F(sizeValue)}>"
                        };
                        InsertOrWrap(open, "</size>");
                    }
                }

                DrawParamRow("Font", ref fontName, n => InsertOrWrap($"<font=\"{n}\">", "</font>"));
                DrawParamRow("Material", ref materialName, n => InsertOrWrap($"<material=\"{n}\">", "</material>"));
                DrawParamRow("Style", ref styleName, n => InsertOrWrap($"<style=\"{n}\">", "</style>"));
                DrawParamRow("Gradient", ref gradientName, n => InsertOrWrap($"<gradient=\"{n}\">", "</gradient>"));
                DrawParamRow("Link Id", ref linkId, n => InsertOrWrap($"<link=\"{n}\">", "</link>"));

                using (new EditorGUILayout.HorizontalScope())
                {
                    spriteName = EditorGUILayout.TextField("Sprite", spriteName);
                    spriteIndex = EditorGUILayout.IntField(spriteIndex, GUILayout.Width(40));
                    if (GUILayout.Button("Name", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertRaw($"<sprite name=\"{spriteName}\">");
                    if (GUILayout.Button("Idx", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertRaw($"<sprite={spriteIndex}>");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    spacingValue = EditorGUILayout.FloatField("Spacing", spacingValue);
                    if (GUILayout.Button("cspace", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertOrWrap($"<cspace={F(spacingValue)}>", "</cspace>");
                    if (GUILayout.Button("mspace", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertOrWrap($"<mspace={F(spacingValue)}>", "</mspace>");
                    if (GUILayout.Button("space", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertRaw($"<space={F(spacingValue)}>");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    vOffsetValue = EditorGUILayout.FloatField("voffset", vOffsetValue);
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertOrWrap($"<voffset={F(vOffsetValue)}em>", "</voffset>");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    indentValue = EditorGUILayout.FloatField("indent %", indentValue);
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertOrWrap($"<indent={F(indentValue)}%>", "</indent>");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    rotateValue = EditorGUILayout.FloatField("rotate", rotateValue);
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        InsertOrWrap($"<rotate={F(rotateValue)}>", "</rotate>");
                }
            }
        }

        private void DrawParamRow(string label, ref string value, Action<string> onInsert)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value ?? "");
                using (new ToolStyles.DisabledScope(string.IsNullOrWhiteSpace(value)))
                {
                    if (GUILayout.Button("Insert", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        onInsert(value.Trim());
                }
            }
        }

        private void DrawTagReference()
        {
            ToolStyles.CardHeader("All tags");
            GUILayout.Space(ToolStyles.SpaceM);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Inset))
            {
                filter = EditorGUILayout.TextField("Filter", filter ?? "");

                string currentCategory = null;
                string activeFilter = (filter ?? "").Trim();

                foreach (var tag in AllTags)
                {
                    // Case-insensitive compare rather than ToLowerInvariant on four fields of every tag - that
                    // allocated ~200 throwaway strings per repaint while the filter had text in it.
                    if (activeFilter.Length > 0)
                    {
                        bool match =
                            ContainsIgnoreCase(tag.Name, activeFilter) ||
                            ContainsIgnoreCase(tag.Category, activeFilter) ||
                            ContainsIgnoreCase(tag.Open, activeFilter) ||
                            ContainsIgnoreCase(tag.Description, activeFilter);
                        if (!match) continue;
                    }

                    if (tag.Category != currentCategory)
                    {
                        currentCategory = tag.Category;
                        EditorGUILayout.LabelField(currentCategory, categoryHeaderStyle);
                    }

                    // The row itself carries the description as its tooltip, so there is no separate help
                    // button - and no horizontal scope, now that the row is a single control.
                    var content = new GUIContent(
                        $"{tag.Name}  {tag.Open}{(string.IsNullOrEmpty(tag.Close) ? "" : " … " + tag.Close)}",
                        tag.Description);

                    if (GUILayout.Button(content, tagButtonStyle))
                    {
                        if (string.IsNullOrEmpty(tag.Close))
                            InsertRaw(tag.Open);
                        else
                            InsertOrWrap(tag.Open, tag.Close);
                    }
                }
            }
        }

        private static TextEditor GetRecycledTextEditor()
        {
            if (!recycledEditorFieldSearched)
            {
                recycledEditorFieldSearched = true;
                // EditorGUILayout.TextArea writes caret into this shared editor, not GetStateObject(...).
                recycledEditorField =
                    typeof(EditorGUI).GetField("s_RecycledEditor", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? typeof(EditorGUI).GetField("activeEditor", BindingFlags.NonPublic | BindingFlags.Static);

                // Without this field the caret cannot be read, and every insert quietly lands at the end of
                // the text instead of at the cursor. Say so rather than degrading in silence, since the only
                // way this breaks is a Unity version renaming an internal field.
                if (recycledEditorField == null)
                {
                    Debug.LogWarning("[TMP Rich Text] Could not locate EditorGUI's internal text editor, so tags " +
                                     "will be appended at the end of the text rather than inserted at the caret. " +
                                     "This Unity version has likely renamed the internal field.");
                }
            }

            return recycledEditorField != null ? recycledEditorField.GetValue(null) as TextEditor : null;
        }

        private void TryCacheCaret()
        {
            var te = GetRecycledTextEditor();
            if (te == null) return;

            string current = richText ?? "";
            string editorText = te.text ?? "";

            // While our composer is focused, always refresh from the recycled editor.
            if (GUI.GetNameOfFocusedControl() == TextControlName)
            {
                cachedCursor = Mathf.Clamp(te.cursorIndex, 0, Mathf.Max(current.Length, editorText.Length));
                cachedSelect = Mathf.Clamp(te.selectIndex, 0, Mathf.Max(current.Length, editorText.Length));
                // Keep indices in range of the string we will edit.
                cachedCursor = Mathf.Clamp(cachedCursor, 0, current.Length);
                cachedSelect = Mathf.Clamp(cachedSelect, 0, current.Length);
                hasCachedCaret = true;
                return;
            }

            // Focus already left the field (e.g. MouseDown on Insert). If the recycled editor still
            // holds our text, grab caret one last time before another control overwrites it.
            if (!string.IsNullOrEmpty(editorText) && editorText == current)
            {
                cachedCursor = Mathf.Clamp(te.cursorIndex, 0, current.Length);
                cachedSelect = Mathf.Clamp(te.selectIndex, 0, current.Length);
                hasCachedCaret = true;
            }
        }

        /// <summary>Always resolves to the caret/selection. Defaults to end of text if never focused.</summary>
        private void GetCaretRange(string text, out int start, out int end)
        {
            // Prefer a live read right before insert (focus may already be gone — then use cache).
            TryCacheCaret();

            if (hasCachedCaret)
            {
                start = Mathf.Clamp(Mathf.Min(cachedCursor, cachedSelect), 0, text.Length);
                end = Mathf.Clamp(Mathf.Max(cachedCursor, cachedSelect), 0, text.Length);
            }
            else
            {
                start = end = text.Length;
            }
        }

        private void ApplyCaretToActiveEditor()
        {
            var te = GetRecycledTextEditor();
            if (te == null) return;

            te.text = richText ?? "";
            int len = te.text.Length;
            te.cursorIndex = Mathf.Clamp(cachedCursor, 0, len);
            te.selectIndex = Mathf.Clamp(cachedSelect, 0, len);
            pendingCaretRestore = false;
        }

        private void TrackTypedTextChange(string edited)
        {
            edited ??= "";
            string previous = richText ?? "";
            if (edited == previous)
                return;

            // Group consecutive typing into one undo step.
            if (!suppressTextTracking && !typingGroupOpen)
            {
                PushUndoState(CaptureState(previous));
                typingGroupOpen = true;
            }

            richText = edited;
            redoStack.Clear();
        }

        private TextState CaptureState(string text = null)
        {
            TryCacheCaret();
            text ??= richText ?? "";
            int cursor = hasCachedCaret ? Mathf.Clamp(cachedCursor, 0, text.Length) : text.Length;
            int select = hasCachedCaret ? Mathf.Clamp(cachedSelect, 0, text.Length) : text.Length;
            return new TextState(text, cursor, select);
        }

        private void PushUndoState(TextState state)
        {
            if (undoStack.Count > 0)
            {
                var top = undoStack[undoStack.Count - 1];
                if (top.Text == state.Text && top.Cursor == state.Cursor && top.Select == state.Select)
                    return;
            }

            undoStack.Add(state);
            if (undoStack.Count > MaxUndoSteps)
                undoStack.RemoveAt(0);

            redoStack.Clear();
        }

        private void PerformUndo()
        {
            if (undoStack.Count == 0)
                return;

            typingGroupOpen = false;
            var current = CaptureState();
            var previous = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add(current);
            ApplyState(previous);
        }

        private void PerformRedo()
        {
            if (redoStack.Count == 0)
                return;

            typingGroupOpen = false;
            var current = CaptureState();
            var next = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(current);
            if (undoStack.Count > MaxUndoSteps)
                undoStack.RemoveAt(0);
            ApplyState(next);
        }

        private void ApplyState(TextState state)
        {
            suppressTextTracking = true;
            CommitTextChange(state.Text, state.Cursor, state.Select, recordUndo: false);
            suppressTextTracking = false;
        }

        private void CommitTextChange(string newText, int newCursor, int newSelect, bool recordUndo = true)
        {
            newText ??= "";
            if (recordUndo && newText != (richText ?? ""))
            {
                PushUndoState(CaptureState());
                typingGroupOpen = false;
            }

            richText = newText;
            cachedCursor = Mathf.Clamp(newCursor, 0, richText.Length);
            cachedSelect = Mathf.Clamp(newSelect, 0, richText.Length);
            hasCachedCaret = true;

            // Prefer in-place update of Unity's recycled TextEditor (keeps caret, refreshes view).
            var te = GetRecycledTextEditor();
            if (te != null && GUI.GetNameOfFocusedControl() == TextControlName)
            {
                te.text = richText;
                te.cursorIndex = cachedCursor;
                te.selectIndex = cachedSelect;
                GUI.changed = true;
                Repaint();
                return;
            }

            // Focus was stolen by the Insert button — release stale buffer, then restore caret.
            forceReleaseTextFocus = true;
            pendingCaretRestore = true;
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
            Repaint();

            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                EditorGUI.FocusTextInControl(TextControlName);
                pendingCaretRestore = true;
                Repaint();

                EditorApplication.delayCall += () =>
                {
                    if (this == null) return;
                    ApplyCaretToActiveEditor();
                    Repaint();
                };
            };
        }

        private void InsertOrWrap(string open, string close)
        {
            string text = richText ?? "";
            GetCaretRange(text, out int start, out int end);

            string selected = text.Substring(start, end - start);
            string replacement;
            int newCursor;
            int newSelect;

            if (wrapSelection)
            {
                // Wrap selection, or insert a tag pair around the caret.
                replacement = open + selected + close;
                if (end > start)
                {
                    newSelect = start;
                    newCursor = start + replacement.Length;
                }
                else
                {
                    // Place caret between open/close so typing continues inside the tags.
                    newCursor = newSelect = start + open.Length;
                }
            }
            else
            {
                // Insert at caret (replaces selection if any).
                replacement = open + close;
                // Leave caret between open/close when both exist; otherwise after the snippet.
                newCursor = newSelect = string.IsNullOrEmpty(close)
                    ? start + replacement.Length
                    : start + open.Length;
            }

            string newText = text.Substring(0, start) + replacement + text.Substring(end);
            CommitTextChange(newText, newCursor, newSelect);
        }

        private void InsertRaw(string snippet)
        {
            string text = richText ?? "";
            GetCaretRange(text, out int start, out int end);

            string newText = text.Substring(0, start) + snippet + text.Substring(end);
            int caret = start + snippet.Length;
            CommitTextChange(newText, caret, caret);
        }

        private static bool ContainsIgnoreCase(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void LoadFromSelectedTmp()
        {
            var tmp = selectedTmps.Count > 0 ? selectedTmps[0] : null;
            if (tmp == null)
            {
                EditorUtility.DisplayDialog("No TMP Selected", "Select a GameObject with a TMP_Text component.", "OK");
                return;
            }

            string loaded = tmp.text ?? "";
            CommitTextChange(loaded, loaded.Length, loaded.Length);
            ShowNotification(new GUIContent($"Loaded from {tmp.name}"));
        }

        private void ApplyToSelectedTmps()
        {
            if (selectedTmps.Count == 0)
            {
                EditorUtility.DisplayDialog("No TMP Selected", "Select one or more GameObjects with TMP_Text components.", "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            foreach (var tmp in selectedTmps)
            {
                if (!tmp) continue;

                // RecordObject already flags the object dirty; SetDirty afterwards was redundant.
                Undo.RecordObject(tmp, "Apply TMP Rich Text");
                tmp.text = richText ?? "";
            }

            Undo.SetCurrentGroupName("Apply TMP Rich Text");
            Undo.CollapseUndoOperations(group);
            ShowNotification(new GUIContent($"Applied to {selectedTmps.Count} TMP(s)"));
        }

        private static string ColorToHex(Color color, bool withAlpha)
        {
            Color32 c = color;
            return withAlpha
                ? $"{c.r:X2}{c.g:X2}{c.b:X2}{c.a:X2}"
                : $"{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        private static string NormalizeAlphaHex(string value)
        {
            value = (value ?? "").Trim().TrimStart('#');
            if (value.Length == 0) return "FF";
            if (value.Length == 1) return "0" + value.ToUpperInvariant();
            return value.Substring(0, Mathf.Min(2, value.Length)).ToUpperInvariant();
        }

        private static string F(float value) => value.ToString(CultureInfo.InvariantCulture);

        private enum SizeMode
        {
            Absolute,
            Plus,
            Percent
        }

        /// <summary>Tags Unity's IMGUI rich text can actually render. Everything else is TMP-only.</summary>
        private static readonly HashSet<string> PreviewSupportedTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "b", "i", "size", "color" };

        private static readonly Regex AnyTagPattern = new Regex(@"<\s*/?\s*([a-zA-Z#][^\s=>/]*)[^>]*>", RegexOptions.Compiled);

        /// <summary>
        /// Approximates the TMP result using IMGUI rich text, which only understands b / i / size / color.
        /// Every other TMP tag would be drawn as literal text, so the preview used to fill up with tag soup
        /// rather than showing the string. Those tags are dropped for display only - richText is untouched.
        /// </summary>
        private static string ConvertToUnityPreview(string tmpText)
        {
            if (string.IsNullOrEmpty(tmpText))
                return " ";

            string text = tmpText.Replace("<br>", "\n").Replace("<BR>", "\n");

            return AnyTagPattern.Replace(text, match =>
            {
                string name = match.Groups[1].Value;

                // <#RRGGBB> is TMP shorthand for <color=#RRGGBB>, which IMGUI does understand.
                if (name.StartsWith("#", StringComparison.Ordinal))
                    return $"<color={name}>";

                return PreviewSupportedTags.Contains(name) ? match.Value : "";
            });
        }

        private readonly struct RichTag
        {
            public readonly string Name;
            public readonly string Category;
            public readonly string Open;
            public readonly string Close;
            public readonly string Description;

            public RichTag(string name, string category, string open, string close, string description)
            {
                Name = name;
                Category = category;
                Open = open;
                Close = close;
                Description = description;
            }
        }

        private readonly struct TextState
        {
            public readonly string Text;
            public readonly int Cursor;
            public readonly int Select;

            public TextState(string text, int cursor, int select)
            {
                Text = text ?? "";
                Cursor = cursor;
                Select = select;
            }
        }
    }
}
