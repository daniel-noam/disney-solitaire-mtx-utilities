using System.Collections.Generic;
using BlueGraph;
using BlueGraph.Editor;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Why the open graph asks for the client version it asks for, inside the graph editor.
    ///
    /// The number alone is not actionable: a template that says 1.23.0 will not reach a player on
    /// 1.21.0, and finding out which of two hundred nodes decided that means opening nodes until
    /// you meet one. This names them and takes you there, without leaving the canvas.
    ///
    /// Put into the window by <see cref="GraphToolPanelInjector"/>, which attaches it from
    /// outside: nothing in the graph editor was changed to make room for this.
    /// </summary>
    public class TemplateVersionPanel : VisualElement
    {
        private const int MaxRows = 60;

        private readonly GraphEditorWindow _window;
        private readonly Label _headline;
        private readonly Label _relief;
        private readonly ScrollView _rows;

        public TemplateVersionPanel(GraphEditorWindow window)
        {
            _window = window;

            // Placement is the window's: registered panels are stacked down the right for it.
            style.width = 280;
            style.maxHeight = 320;
            style.paddingTop = 8;
            style.paddingBottom = 8;
            style.paddingLeft = 8;
            style.paddingRight = 8;

            style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.92f);
            SetBorder(1, new Color(1f, 1f, 1f, 0.35f));

            var title = new Label("Min Version") { style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold } };
            Add(title);

            _headline = new Label { style = { fontSize = 18, unityFontStyleAndWeight = FontStyle.Bold } };
            Add(_headline);

            _relief = new Label { style = { fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            _relief.style.color = new Color(0.7f, 0.7f, 0.7f);
            Add(_relief);

            _rows = new ScrollView { style = { flexGrow = 1 } };
            Add(_rows);

            var refresh = new Button(Rebuild) { text = "Refresh" };
            refresh.style.fontSize = 10;
            refresh.style.marginTop = 4;
            Add(refresh);
        }

        /// <summary>
        /// Rebuilt when the panel is switched on, and by its own button, rather than every frame:
        /// the walk covers every node of every subgraph, which is not a thing to repeat sixty
        /// times a second for a number that only moves when a node is added.
        /// </summary>
        public void Rebuild()
        {
            _rows.Clear();

            var graph = _window == null ? null : _window.Graph;
            var contributors = TemplateVersionAnalyzer.Analyze(graph);
            var version = TemplateVersionAnalyzer.Resolve(contributors);
            var without = TemplateVersionAnalyzer.Without(contributors, version);

            _headline.text = version;

            var top = 0;
            foreach (var contributor in contributors)
                if (contributor.Version == version) top++;

            // The one fact the bare number cannot give you: what clearing the top tier would buy,
            // which is nothing at all unless you clear all of it.
            _relief.text = top == 0
                ? "Nothing here raises it above the default."
                : without == version
                    ? $"{top} at {version}."
                    : $"Without the {top} at {version}, it would be {without}.";

            // Highest first, through GetMaxVersion rather than by comparing the strings: "1.9.0"
            // sorts above "1.23.0" alphabetically, and the release order is what decides.
            contributors.Sort((a, b) =>
            {
                if (a.Version != b.Version)
                    return SolitaireVersions.GetMaxVersion(a.Version, b.Version) == a.Version ? -1 : 1;

                return string.CompareOrdinal(a.Name, b.Name);
            });

            var shown = Mathf.Min(MaxRows, contributors.Count);
            for (var i = 0; i < shown; i++) _rows.Add(Row(contributors[i], contributors[i].Version == version));

            if (contributors.Count > shown)
                _rows.Add(new Label($"…and {contributors.Count - shown} more")
                {
                    style = { fontSize = 10, color = new Color(0.6f, 0.6f, 0.6f) }
                });
        }

        private VisualElement Row(VersionContributor contributor, bool top)
        {
            // The whole row is the button: a target the width of the panel beats hunting for a
            // four-character link.
            var row = new Button(() => Show(contributor));
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginLeft = 0;
            row.style.marginRight = 0;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.paddingLeft = 4;
            row.style.paddingRight = 4;
            row.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);

            // The ones below the top version are along for the ride; dimming says so without
            // hiding them, because they are what the number drops to next.
            var text = top ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.6f, 0.6f, 0.6f);

            var version = new Label(contributor.Version)
            {
                style = { fontSize = 10, width = 52, color = top ? new Color(1f, 0.8f, 0.4f) : text }
            };
            row.Add(version);

            var name = new Label(contributor.Name) { style = { fontSize = 11, flexGrow = 1, color = text } };
            name.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(name);

            if (!string.IsNullOrEmpty(contributor.Path))
            {
                row.tooltip = "Inside " + contributor.Path;
                var badge = new Label("↴") { style = { fontSize = 10, color = text } };
                row.Add(badge);
            }

            return row;
        }

        /// <summary>
        /// Frames the node in whichever graph actually holds it. For a node inside a subgraph that
        /// is the subgraph's own window, not this one — pointing at the subgraph node instead would
        /// land you on something only guilty by association.
        /// </summary>
        private void Show(VersionContributor contributor)
        {
            if (contributor.Graph == null || contributor.Node == null) return;

            var window = contributor.Graph == _window.Graph
                ? _window
                : GraphEditor.GetExistingEditorWindow(contributor.Graph)
                  ?? GraphEditor.CreateEditorWindow(contributor.Graph);

            if (window == null) return;
            window.Focus();

            GraphNavigation.Focus(window.Canvas, contributor.Node.TabIndex, contributor.Node.ID);
        }

        private void SetBorder(float width, Color colour)
        {
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
            style.borderTopColor = colour;
            style.borderBottomColor = colour;
            style.borderLeftColor = colour;
            style.borderRightColor = colour;
        }
    }
}
