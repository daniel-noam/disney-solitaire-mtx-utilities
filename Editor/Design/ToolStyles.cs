// The shared look of the LinkedAssets editor tools.
//
// Extracted from EasyUpload once its design had settled, so what is here is a set of decisions that
// were argued with in a real window rather than guessed at in the abstract. The rules below are the
// part worth keeping; the styles are just how they are enforced.
//
//   1. Two button levels, never three. Primary is the one action a panel exists for; Secondary is
//      every other button. A third, quieter level was tried and removed: it read as not-clickable
//      beside the buttons it sat next to, and being low-contrast already, it had nothing left to
//      give up when it needed to look disabled. Hierarchy belongs in *which* action is primary, not
//      in making some buttons whisper.
//
//   2. A style that is not for a button gets its interactive states stripped — see Inert. GUI.Label
//      asks its style to draw hovered whenever the pointer is inside its rect, so a label built from
//      EditorStyles.label lights up under the cursor and lies about being clickable.
//
//   3. Disabled has to be visible — see DisabledScope. IMGUI's own disabled tint is calibrated for
//      the built-in skin and barely shows through a custom background texture.
//
//   4. Spacing, control heights and button widths come from the scale below. Nothing should invent
//      a number; if a new size is genuinely needed, it belongs here with a name.
//
//   5. A label that cannot wrap must be given a rect and elided into it. A non-wrapping label
//      reports its content width as its *minimum* width, so a long path or ARN does not get clipped
//      — it pushes the window wider than the screen.
//
// Three habits go with these, which no style can enforce on a tool's behalf:
//
//   0. Call Ensure() first, in every entry point that reads one of these — a window's OnGUI, an
//      inspector's OnInspectorGUI, a drawer's OnGUI. The styles are built on demand and released
//      on every assembly reload, so anything that has not called it can be handed a null style,
//      and IMGUI answers a null style with a NullReferenceException from inside its own layout.
//
//   * Set wantsMouseMove and repaint on MouseMove, or hover states only appear when something else
//     happens to trigger a frame, and every button feels a tenth of a second behind the pointer.
//
//   * Freeze anything that decides whether a control exists once per event pass. IMGUI runs Layout
//     and Repaint over the same code and requires both to emit the same controls; a value a worker
//     thread can change between them throws "Getting control N's position in a group with only M
//     controls" and takes the window down.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// The look of the EasyUpload windows: palette, rounded-rect textures, and the styles built on
    /// them.
    ///
    /// IMGUI has no rounded corners and no border of its own, so the boxes are 9-sliced textures
    /// generated once at the size they are drawn. It is a little machinery for a lot of difference
    /// — the alternative is a stack of grey helpBoxes that all read as the same thing.
    /// </summary>
    public static class ToolStyles
    {
        // ---- metrics ----
        //
        // The window had six spacing values, four button heights and thirteen hard-coded widths,
        // all chosen one at a time. These are those decisions made once. Anything laid out by hand
        // should reach for one of these rather than a fresh number.

        public const float SpaceXS = 2f;
        public const float SpaceS = 4f;
        public const float SpaceM = 6f;
        public const float SpaceL = 8f;
        public const float SpaceXL = 12f;

        /// <summary>Secondary and quiet buttons, popups, inline fields.</summary>
        public const float ControlHeight = 20f;

        /// <summary>The one action a panel is for: Review, Upload, Connect.</summary>
        public const float ActionHeight = 24f;

        /// <summary>A row in a virtualised list.</summary>
        public const float ListRowHeight = 21f;

        /// <summary>A control sitting inside a list row, which the row height constrains.</summary>
        public const float InRowHeight = 16f;

        public const float ButtonS = 56f;
        public const float ButtonM = 84f;
        public const float ButtonL = 132f;

        /// <summary>A square button holding one glyph.</summary>
        public const float IconWidth = 24f;

        /// <summary>A search or text field sitting in a row rather than filling it.</summary>
        public const float FieldWidth = 160f;

        /// <summary>A short right-aligned count or status word.</summary>
        public const float MetaWidth = 70f;

        public const float PopupWidth = 72f;

        /// <summary>The label column of a hand-built form row.</summary>
        public const float FormLabelWidth = 90f;

        public const float TabWidth = 64f;

        /// <summary>A multi-line text field: enough to read a few lines without dominating a panel.</summary>
        public const float TextAreaHeight = 62f;

        /// <summary>A drop target big enough to aim at, whether or not it currently holds anything.</summary>
        public const float DropZoneHeight = 52f;

        private static bool built;
        private static bool builtPro;

        // ---- palette ----
        public static Color WindowBg, CardBg, CardBorder, InsetBg, InsetBorder;
        public static Color Text, Muted, Faint;
        public static Color Accent, AccentHover, OnAccent;
        public static Color Ok, Warn, Err;

        // ---- styles ----
        public static GUIStyle Card;
        public static GUIStyle Inset;
        public static GUIStyle Pill;
        public static GUIStyle Tag;
        public static GUIStyle TagClose;
        public static GUIStyle BadgeNumber;
        public static GUIStyle CardTitle;
        public static GUIStyle Hint;
        public static GUIStyle Mono;
        public static GUIStyle MonoSmall;
        public static GUIStyle PathValue;
        public static GUIStyle Placeholder;
        public static GUIStyle Primary;
        public static GUIStyle Secondary;
        public static GUIStyle SecondaryCompact;
        public static GUIStyle RowLabel;
        public static GUIStyle StatusText;
        public static GUIStyle ColumnHeader;
        public static GUIStyle ColumnHeaderRight;
        public static GUIStyle TextArea;

        private static readonly List<Texture2D> Owned = new List<Texture2D>();
        private static Font monoFont;

        /// <summary>Sizes every step badge alike, whichever digit it holds.</summary>
        private static readonly GUIContent ReferenceDigit = new GUIContent("0");

        /// <summary>
        /// The textures and the font are HideAndDontSave, which means Unity will not collect them —
        /// without this a fresh set leaks on every script reload.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting -= Release;
            EditorApplication.quitting += Release;
        }

        private static void Release()
        {
            foreach (var texture in Owned)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            Owned.Clear();
            Circles.Clear();
            CentredCache.Clear();
            if (monoFont != null) UnityEngine.Object.DestroyImmediate(monoFont);
            monoFont = null;
            built = false;
        }

        /// <summary>Call once at the top of OnGUI. Rebuilds when the editor skin changes.</summary>
        public static void Ensure()
        {
            if (built && builtPro == EditorGUIUtility.isProSkin && Card != null && Card.normal.background != null)
                return;

            Release();

            var pro = EditorGUIUtility.isProSkin;
            builtPro = pro;

            if (pro)
            {
                WindowBg = Hex("2E2E2E");
                CardBg = Hex("3A3A3A");
                CardBorder = Hex("232323");
                InsetBg = Hex("2B2B2B");
                InsetBorder = Hex("1F1F1F");
                Text = Hex("D6D6D6");
                Muted = Hex("9B9B9B");
                Faint = Hex("6E6E6E");
                Accent = Hex("4C8EDA");
                AccentHover = Hex("5C9CE6");
                OnAccent = Hex("FFFFFF");
                Ok = Hex("5BB974");
                Warn = Hex("E0A63C");
                Err = Hex("E0645A");
            }
            else
            {
                WindowBg = Hex("C8C8C8");
                CardBg = Hex("E8E8E8");
                CardBorder = Hex("A6A6A6");
                InsetBg = Hex("DCDCDC");
                InsetBorder = Hex("B4B4B4");
                Text = Hex("1E1E1E");
                Muted = Hex("5A5A5A");
                Faint = Hex("858585");
                Accent = Hex("2C6DB5");
                AccentHover = Hex("3A7CC4");
                OnAccent = Hex("FFFFFF");
                Ok = Hex("2F8A4C");
                Warn = Hex("A9741B");
                Err = Hex("C0392B");
            }

            Card = BoxStyle(6, CardBg, CardBorder, 1, new RectOffset(12, 12, 10, 12));
            Inset = BoxStyle(4, InsetBg, InsetBorder, 1, new RectOffset(8, 8, 6, 6));
            Pill = BoxStyle(9, Blend(CardBg, WindowBg, 0.5f), CardBorder, 1, new RectOffset(9, 9, 3, 3));
            Tag = BoxStyle(4, Blend(Accent, CardBg, 0.72f), Blend(Accent, CardBorder, 0.5f), 1,
                new RectOffset(7, 4, 3, 3));

            monoFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" }, 11);

            CardTitle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
            };
            CardTitle.normal.textColor = Text;

            Hint = new GUIStyle(EditorStyles.label) { fontSize = 10, wordWrap = true, richText = true };
            Hint.normal.textColor = Muted;

            RowLabel = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
            RowLabel.normal.textColor = Text;

            StatusText = new GUIStyle(EditorStyles.label) { fontSize = 10, alignment = TextAnchor.MiddleRight };
            StatusText.normal.textColor = Muted;

            // One size and weight for every column heading, whatever side of the row it sits on.
            // Mixing the body label with the right-aligned meta label gave three headings in two
            // different sizes, which is what made the bar look assembled rather than designed.
            ColumnHeader = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
            };
            // The same colour as a card title: a column heading is a heading, and there is no
            // reason for two kinds of heading in one window to read at two different strengths.
            ColumnHeader.normal.textColor = Text;

            ColumnHeaderRight = new GUIStyle(ColumnHeader) { alignment = TextAnchor.MiddleRight };

            Mono = new GUIStyle(EditorStyles.label) { font = monoFont, fontSize = 11, richText = false };
            Mono.normal.textColor = Text;

            MonoSmall = new GUIStyle(Mono) { fontSize = 10 };
            MonoSmall.normal.textColor = Muted;

            PathValue = new GUIStyle(Mono) { wordWrap = false, clipping = TextClipping.Clip };

            // Built from textArea, not textField: EditorGUILayout.TextArea's convenience overload
            // uses textField, which does not word-wrap. One long unbroken line then reports a huge
            // minimum width and drags the whole window open. Always pass this style explicitly.
            TextArea = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                font = monoFont,
                fontSize = 10,
            };

            Placeholder = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Italic };
            Placeholder.normal.textColor = Faint;

            TagClose = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 1),
            };
            TagClose.normal.textColor = Muted;
            TagClose.hover.textColor = Err;

            // Monospaced, and zero padding so CalcSize returns the glyph box and nothing else.
            //
            // The font matters here: in a proportional face "1" has a narrower advance than "2" and
            // sits asymmetrically inside it, so centring the measured box still leaves the stroke
            // visibly off to one side. A monospaced face gives every digit the same advance with the
            // glyph centred in it, which is the only way three badges can look identically centred.
            BadgeNumber = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            if (monoFont != null) BadgeNumber.font = monoFont;

            // Two levels, not three. Primary is the one action a panel exists for; Secondary is
            // every other button. There was a third, quieter level for small inline actions, and it
            // was a mistake twice over: it read as not-clickable next to the buttons it sat beside,
            // and being low-contrast to begin with there was nothing left to take away when it had
            // to look disabled. A button that can be pressed should look like the others.
            Primary = ButtonStyle(Accent, AccentHover, OnAccent);
            Secondary = ButtonStyle(Blend(CardBg, WindowBg, 0.35f), Blend(CardBg, Accent, 0.75f), Text);

            // The same button, sized for a list row. A size, not a third appearance.
            SecondaryCompact = new GUIStyle(Secondary)
            {
                fontSize = 10,
                padding = new RectOffset(4, 4, 0, 0),
            };

            // GUI.Label asks the style to draw with hover whenever the pointer is inside its rect,
            // and these all descend from EditorStyles.label, which carries one. It never showed
            // while the window only repainted on clicks; now that it repaints on mouse move, every
            // heading and hint lights up under the cursor and reads as clickable. None of them are.
            //
            // TagClose is deliberately absent: its × is a button and should respond.
            Inert(CardTitle);
            Inert(Hint);
            Inert(RowLabel);
            Inert(StatusText);
            Inert(ColumnHeader);
            Inert(ColumnHeaderRight);
            Inert(Mono);
            Inert(MonoSmall);
            Inert(PathValue);
            Inert(Placeholder);
            Inert(BadgeNumber);

            built = true;
        }

        /// <summary>
        /// A disabled scope that visibly dims what it wraps.
        ///
        /// IMGUI's own disabled tint is calibrated for the built-in skin and barely registers on a
        /// custom style with its own background texture — an unavailable button
        /// ends up looking exactly like an available one. Fading the whole scope makes "you cannot
        /// press this" unmistakable — gently, because a disabled button still has to be readable.
        /// </summary>
        public readonly struct DisabledScope : IDisposable
        {
            private readonly Color previousColor;
            private readonly bool previousEnabled;
            private readonly bool dimmed;

            public DisabledScope(bool disabled)
            {
                previousEnabled = GUI.enabled;
                previousColor = GUI.color;
                dimmed = disabled;

                if (!disabled) return;
                GUI.enabled = false;
                GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b,
                    previousColor.a * 0.6f);
            }

            public void Dispose()
            {
                if (!dimmed) return;
                GUI.enabled = previousEnabled;
                GUI.color = previousColor;
            }
        }

        /// <summary>Removes the hover and pressed states a label style has no business having.</summary>
        private static GUIStyle Inert(GUIStyle style)
        {
            var colour = style.normal.textColor;

            style.hover.background = null;
            style.hover.textColor = colour;
            style.active.background = null;
            style.active.textColor = colour;
            style.focused.background = null;
            style.focused.textColor = colour;
            style.onNormal.background = null;
            style.onNormal.textColor = colour;
            style.onHover.background = null;
            style.onHover.textColor = colour;
            style.onActive.background = null;
            style.onActive.textColor = colour;

            return style;
        }

        // ---------- drawing helpers ----------

        /// <summary>
        /// A plain card header: a title and the room to its right for the card's actions.
        ///
        /// The default. A number belongs on a panel only when the panels are steps taken in order —
        /// numbering panels that are simply panels tells the reader to look for a sequence that is
        /// not there.
        /// </summary>
        public static Rect CardHeader(string title)
        {
            var rect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(rect.x, rect.y, rect.width, rect.height), title, CardTitle);
            return rect;
        }

        /// <summary>A card header for one step of a sequence, with its number in a badge.</summary>
        public static Rect CardHeader(int step, string title, bool done)
        {
            var rect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            var badge = new Rect(rect.x, rect.y + 2, 18, 18);

            // Drawn as a stretched circle texture rather than a 9-sliced box: a 9-slice whose
            // borders total more than the rect it is drawn into (22px of border in an 18px badge)
            // has no middle left to stretch, so the corners get squashed and the "circle" lands
            // off-centre inside its own rect.
            var previousColor = GUI.color;
            GUI.color = done ? Accent : Blend(CardBg, WindowBg, 0.35f);
            GUI.DrawTexture(badge, Circle(32));
            GUI.color = previousColor;

            // Centred by measurement. A MiddleCenter label centres against the font's line box,
            // which carries ascender and descender space the digit does not use, so the glyph sits
            // visibly low in a box this small.
            //
            // Width comes from a reference digit rather than the actual one, so every badge gets the
            // same box whatever number is in it — one badge cannot end up a pixel off from the next.
            var content = new GUIContent(step.ToString());
            var size = BadgeNumber.CalcSize(content);
            var width = BadgeNumber.CalcSize(ReferenceDigit).x;
            var textRect = new Rect(
                Mathf.Round(badge.center.x - width / 2f),
                Mathf.Round(badge.center.y - size.y / 2f),
                width, size.y);

            var previousContent = GUI.contentColor;
            GUI.contentColor = done ? OnAccent : Muted;
            GUI.Label(textRect, content, BadgeNumber);
            GUI.contentColor = previousContent;

            GUI.Label(new Rect(rect.x + 25, rect.y, rect.width - 25, rect.height), title, CardTitle);
            return rect;
        }

        /// <summary>An inset value box; returns the inner rect so the caller can draw into it.</summary>
        public static void ValueBox(string value, string placeholder, float height = 22f)
        {
            var rect = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, Inset);
            var inner = new Rect(rect.x + 8, rect.y, rect.width - 16, rect.height);
            var empty = string.IsNullOrEmpty(value);
            GUI.Label(inner, new GUIContent(empty ? placeholder : value, empty ? "" : value),
                empty ? Placeholder : PathValue);
        }

        /// <summary>A status pill with a coloured dot. Returns true when clicked.</summary>
        /// <param name="measureAs">
        /// Text to size the pill by, when the label itself changes from frame to frame. Without it
        /// an animating label makes the pill breathe in and out.
        /// </param>
        public static bool StatusPill(string label, Color dot, string tooltip = "", string measureAs = null)
        {
            // Measured with the style it is drawn in, not with the box style — Pill carries the
            // background and padding but no font, so measuring with it sizes the pill to the wrong text.
            var content = new GUIContent(label, tooltip);
            var textWidth = Hint.CalcSize(new GUIContent(measureAs ?? label)).x;
            var width = textWidth + 30f;
            var rect = GUILayoutUtility.GetRect(width, 18, GUILayout.Width(width));

            GUI.Box(rect, GUIContent.none, Pill);
            Dot(new Rect(rect.x + 9, rect.y + 6, 6, 6), dot);
            GUI.Label(new Rect(rect.x + 21, rect.y, textWidth + 4, rect.height), content, Hint);

            return GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none);
        }

        /// <summary>A removable bucket tag. Returns true when its × is clicked.</summary>
        public static bool RemovableTag(string label)
        {
            var content = new GUIContent(label);
            var textWidth = MonoSmall.CalcSize(content).x;
            var width = textWidth + 30;
            var rect = GUILayoutUtility.GetRect(width, 19, GUILayout.Width(width));

            GUI.Box(rect, GUIContent.none, Tag);
            GUI.Label(new Rect(rect.x + 7, rect.y + 1, textWidth + 2, 17), content, MonoSmall);

            var closeRect = new Rect(rect.xMax - 19, rect.y + 1, 16, 17);
            GUI.Label(closeRect, "×", TagClose);
            EditorGUIUtility.AddCursorRect(closeRect, MouseCursor.Link);
            return GUI.Button(closeRect, new GUIContent("", "Remove " + label), GUIStyle.none);
        }

        /// <summary>
        /// Draws a label in exactly the colour asked for.
        ///
        /// GUI.contentColor *multiplies* the style's own colour, so tinting a muted style with an
        /// accent gives a muted accent — the label comes out darker than the colour requested and
        /// looks washed rather than coloured. This swaps the style's colour for the draw and puts it
        /// back, which is the only way to get the colour you actually named.
        /// </summary>
        public static void ColouredLabel(Rect rect, GUIContent content, GUIStyle style, Color colour)
        {
            var saved = Recolour(style, colour);
            GUI.Label(rect, content, style);
            Restore(style, saved);
        }

        /// <summary>Every text colour a style carries, so recolouring can put them all back.</summary>
        private struct StateColours
        {
            public Color Normal, Hover, Active, Focused, OnNormal, OnHover, OnActive;
        }

        /// <summary>
        /// Sets *every* state to one colour, not just normal.
        ///
        /// Inert has already pinned hover and active to the style's original colour, so recolouring
        /// only `normal` leaves the old colour waiting in the hover state — and the label changes
        /// colour under the pointer, which is exactly the thing Inert exists to prevent.
        /// </summary>
        private static StateColours Recolour(GUIStyle style, Color colour)
        {
            var saved = new StateColours
            {
                Normal = style.normal.textColor,
                Hover = style.hover.textColor,
                Active = style.active.textColor,
                Focused = style.focused.textColor,
                OnNormal = style.onNormal.textColor,
                OnHover = style.onHover.textColor,
                OnActive = style.onActive.textColor,
            };

            style.normal.textColor = colour;
            style.hover.textColor = colour;
            style.active.textColor = colour;
            style.focused.textColor = colour;
            style.onNormal.textColor = colour;
            style.onHover.textColor = colour;
            style.onActive.textColor = colour;
            return saved;
        }

        private static void Restore(GUIStyle style, StateColours saved)
        {
            style.normal.textColor = saved.Normal;
            style.hover.textColor = saved.Hover;
            style.active.textColor = saved.Active;
            style.focused.textColor = saved.Focused;
            style.onNormal.textColor = saved.OnNormal;
            style.onHover.textColor = saved.OnHover;
            style.onActive.textColor = saved.OnActive;
        }

        public static void ColouredLabel(Rect rect, string text, GUIStyle style, Color colour) =>
            ColouredLabel(rect, new GUIContent(text), style, colour);

        /// <summary>The layout-driven version, for a coloured label inside a row.</summary>
        public static void ColouredLabel(string text, GUIStyle style, Color colour,
            params GUILayoutOption[] options)
        {
            var saved = Recolour(style, colour);
            GUILayout.Label(text, style, options);
            Restore(style, saved);
        }

        /// <summary>The width a vertical scrollbar takes, asked of the skin rather than guessed at.</summary>
        public static float ScrollbarWidth =>
            GUI.skin != null && GUI.skin.verticalScrollbar != null
                ? GUI.skin.verticalScrollbar.fixedWidth + 2f
                : 15f;

        /// <summary>
        /// How wide the rows of a virtualised list should be: the view width, less the scrollbar
        /// gutter only when the list is long enough to have one.
        ///
        /// Reserving the gutter unconditionally is the obvious thing to write and leaves a dead
        /// strip down the right of every list short enough not to scroll.
        /// </summary>
        public static float ListContentWidth(Rect view, int rowCount, float rowHeight) =>
            view.width - (rowCount * rowHeight > view.height ? ScrollbarWidth : 0f);

        /// <summary>A filled circle, antialiased enough for 6–10 px dots.</summary>
        public static void Dot(Rect rect, Color color)
        {
            var texture = Circle(Mathf.Max(3, Mathf.RoundToInt(rect.width)));
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, texture);
            GUI.color = previous;
        }

        /// <summary>
        /// A dashed outline, drawn as short solid runs. IMGUI cannot stroke a dashed path, and a
        /// dashed edge is the one shape that reads unmistakably as "drop something here".
        /// </summary>
        public static void DashedBorder(Rect rect, Color color, float dash = 5f, float gap = 4f, float thickness = 1f)
        {
            var step = dash + gap;
            for (var x = rect.x; x < rect.xMax; x += step)
            {
                var w = Mathf.Min(dash, rect.xMax - x);
                EditorGUI.DrawRect(new Rect(x, rect.y, w, thickness), color);
                EditorGUI.DrawRect(new Rect(x, rect.yMax - thickness, w, thickness), color);
            }
            for (var y = rect.y; y < rect.yMax; y += step)
            {
                var h = Mathf.Min(dash, rect.yMax - y);
                EditorGUI.DrawRect(new Rect(rect.x, y, thickness, h), color);
                EditorGUI.DrawRect(new Rect(rect.xMax - thickness, y, thickness, h), color);
            }
        }

        public static void Divider(float padding = 6f)
        {
            GUILayout.Space(padding);
            var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, CardBorder);
            GUILayout.Space(padding);
        }

        /// <summary>Fills the whole window with the background colour, behind everything else.</summary>
        public static void Backdrop(Rect position)
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), WindowBg);
        }

        public static Color Blend(Color a, Color b, float t) => Color.Lerp(a, b, t);

        /// <summary>The longest ellipsis frame, for measuring a label that is going to animate.</summary>
        public const string EllipsisWidest = "...";

        private static readonly string[] EllipsisFrames = { "...", "..", ".", ".." };

        /// <summary>
        /// The animated tail for a label that means "working": ••• •• • •• and round again.
        ///
        /// Driven by the editor clock rather than by a frame counter, so it runs at one speed
        /// whatever the repaint rate happens to be — and two labels animating at once stay in step
        /// instead of drifting apart.
        /// </summary>
        public static string Ellipsis()
        {
            var index = (int)(EditorApplication.timeSinceStartup * 3.0) % EllipsisFrames.Length;
            return EllipsisFrames[index];
        }

        /// <summary>
        /// Shortens from the middle, keeping both ends. An ARN and a file path are both identified
        /// by their head and their tail, so a plain truncation loses the half that tells you which
        /// one it is.
        /// </summary>
        public static string Elide(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars < 8 || text.Length <= maxChars) return text;
            var head = (maxChars - 1) / 2;
            var tail = maxChars - 1 - head;
            return text.Substring(0, head) + "…" + text.Substring(text.Length - tail);
        }

        /// <summary>Roughly how many characters of <see cref="MonoSmall"/> fit in a pixel width.</summary>
        public static int MonoCharsFor(float width) => Mathf.Max(12, Mathf.FloorToInt(width / 6.1f));

        private static readonly Dictionary<GUIStyle, GUIStyle> CentredCache = new Dictionary<GUIStyle, GUIStyle>();

        /// <summary>A centred copy of a style, cached — building one per repaint allocates on every frame.</summary>
        public static GUIStyle Centred(GUIStyle source)
        {
            if (CentredCache.TryGetValue(source, out var cached) && cached != null) return cached;
            var centred = new GUIStyle(source) { alignment = TextAnchor.MiddleCenter };
            CentredCache[source] = centred;
            return centred;
        }

        // ---------- texture generation ----------

        private static GUIStyle BoxStyle(int radius, Color fill, Color border, int borderWidth, RectOffset padding)
        {
            var texture = RoundedRect(radius, fill, border, borderWidth);
            var style = new GUIStyle
            {
                normal = { background = texture },
                border = new RectOffset(radius + 2, radius + 2, radius + 2, radius + 2),
                padding = padding,
            };
            return style;
        }

        private static GUIStyle ButtonStyle(Color fill, Color hover, Color text)
        {
            var style = new GUIStyle(GUIStyle.none)
            {
                normal = { background = RoundedRect(4, fill, fill, 0), textColor = text },
                hover = { background = RoundedRect(4, hover, hover, 0), textColor = text },
                active = { background = RoundedRect(4, Blend(fill, Color.black, 0.2f), fill, 0), textColor = text },
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(12, 12, 0, 0),
                // Built from GUIStyle.none, which has no margin at all. Unity's own buttons carry
                // one, so replacing them removed every natural gap and left buttons touching in
                // every row in every tool. Layout spacing is the style's job, not the caller's.
                margin = new RectOffset(2, 2, 2, 2),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
            };

            // The "on" states, for when one of these is used as a GUILayout.Toggle rather than a
            // button. Without them a toggled button draws with no background at all and looks
            // exactly like an untoggled one — the control works, it just never says so.
            style.onNormal.background = RoundedRect(4, Accent, Accent, 0);
            style.onNormal.textColor = OnAccent;
            style.onHover.background = RoundedRect(4, AccentHover, AccentHover, 0);
            style.onHover.textColor = OnAccent;
            style.onActive.background = RoundedRect(4, Blend(Accent, Color.black, 0.2f), Accent, 0);
            style.onActive.textColor = OnAccent;

            return style;
        }

        private static readonly Dictionary<int, Texture2D> Circles = new Dictionary<int, Texture2D>();

        private static Texture2D Circle(int size)
        {
            if (Circles.TryGetValue(size, out var cached) && cached != null) return cached;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            var radius = size / 2f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius));
                var alpha = Mathf.Clamp01(radius - distance);
                texture.SetPixel(x, y, new Color(1, 1, 1, alpha));
            }
            texture.Apply();
            Circles[size] = texture;
            Owned.Add(texture);
            return texture;
        }

        /// <summary>
        /// A rounded rectangle sized for 9-slicing: corners at their natural size and a single pixel
        /// of stretchable middle, so one texture draws a box of any dimensions.
        /// </summary>
        private static Texture2D RoundedRect(int radius, Color fill, Color border, int borderWidth)
        {
            var size = radius * 2 + 5;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var half = size / 2f;
            var inner = half - radius;

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Signed distance to a rounded box: negative inside, zero on the edge.
                var px = Mathf.Abs(x + 0.5f - half) - inner;
                var py = Mathf.Abs(y + 0.5f - half) - inner;
                var outside = new Vector2(Mathf.Max(px, 0), Mathf.Max(py, 0)).magnitude;
                var distance = outside + Mathf.Min(Mathf.Max(px, py), 0) - radius;

                var coverage = Mathf.Clamp01(0.5f - distance);
                if (coverage <= 0f)
                {
                    texture.SetPixel(x, y, Color.clear);
                    continue;
                }

                var color = borderWidth > 0 && distance > -borderWidth ? border : fill;
                texture.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * coverage));
            }

            texture.Apply();
            Owned.Add(texture);
            return texture;
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            return color;
        }
    }
}
