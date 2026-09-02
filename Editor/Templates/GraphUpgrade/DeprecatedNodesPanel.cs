using System.Collections.Generic;
using BlueGraph;
using BlueGraph.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// The deprecated nodes in the open graph, and a button to swap each for its replacement.
    ///
    /// The graph already labels these nodes "(Deprecated)" one at a time; what it cannot tell you
    /// is how many there are, or move the wiring for you. Replacing one by hand means rebuilding
    /// every edge it had, which is why they tend to stay.
    /// </summary>
    public class DeprecatedNodesPanel : VisualElement
    {
        private const int MaxRows = 40;

        private readonly GraphEditorWindow _window;
        private readonly Label _headline;
        private readonly ScrollView _rows;
        private readonly Button _upgradeAll;

        private List<NodeUpgrade> _found = new List<NodeUpgrade>();

        /// <summary>
        /// What earlier swaps cut and could not put back. Kept apart from the scan: a rescan only
        /// knows what is deprecated now, and once a node has been replaced its loose ends are no
        /// longer visible anywhere — which is exactly when you need to be told about them.
        /// </summary>
        private readonly List<Loose> _loose = new List<Loose>();

        private sealed class Loose
        {
            public string Node;
            public string NodeId;
            public byte Tab;
            public string Detail;
        }

        private readonly VisualElement _warnings = new VisualElement();

        public DeprecatedNodesPanel(GraphEditorWindow window)
        {
            _window = window;

            style.width = 300;
            style.maxHeight = 340;
            style.paddingTop = 8;
            style.paddingBottom = 8;
            style.paddingLeft = 8;
            style.paddingRight = 8;

            style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.92f);
            SetBorder(1, new Color(1f, 1f, 1f, 0.35f));

            Add(new Label("Deprecated") { style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold } });

            _headline = new Label { style = { fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            _headline.style.color = new Color(0.7f, 0.7f, 0.7f);
            Add(_headline);

            Add(_warnings);

            _rows = new ScrollView { style = { flexGrow = 1 } };
            Add(_rows);

            _upgradeAll = new Button(UpgradeClean) { text = "Upgrade the clean ones" };
            _upgradeAll.style.fontSize = 10;
            _upgradeAll.style.marginTop = 4;
            Add(_upgradeAll);

            var refresh = new Button(Rebuild) { text = "Refresh" };
            refresh.style.fontSize = 10;
            Add(refresh);
        }

        public void Rebuild()
        {
            _rows.Clear();
            DrawWarnings();

            _found = DeprecatedNodeUpgrader.Scan(_window == null ? null : _window.Graph);

            var clean = 0;
            var stuck = 0;
            var guessed = 0;
            foreach (var upgrade in _found)
            {
                if (upgrade.IsClean) clean++;
                else if (!upgrade.CanUpgrade) stuck++;

                if (upgrade.CanUpgrade && !upgrade.Declared) guessed++;
            }

            _headline.text = _found.Count == 0
                ? "None in this graph."
                : $"{_found.Count} deprecated  ·  {clean} swap cleanly" +
                  (stuck > 0 ? $"  ·  {stuck} with nowhere to go" : string.Empty) +
                  (guessed > 0 ? $"\n{guessed} matched by name, not declared — marked ~" : string.Empty);

            _upgradeAll.SetEnabled(clean > 0);
            _upgradeAll.text = clean > 0 ? $"Upgrade the {clean} clean ones" : "Upgrade the clean ones";

            var shown = Mathf.Min(MaxRows, _found.Count);
            for (var i = 0; i < shown; i++) _rows.Add(Row(_found[i]));

            if (_found.Count > shown)
                _rows.Add(new Label($"…and {_found.Count - shown} more")
                {
                    style = { fontSize = 10, color = new Color(0.6f, 0.6f, 0.6f) }
                });
        }

        /// <summary>
        /// The loose ends left by swaps in this session, above the list rather than in it: they are
        /// work to do, and the list below is only what is left to swap.
        /// </summary>
        private void DrawWarnings()
        {
            _warnings.Clear();
            if (_loose.Count == 0) return;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 2 } };

            var title = new Label($"{_loose.Count} connection" + (_loose.Count == 1 ? "" : "s") + " left loose")
            {
                style = { fontSize = 10, flexGrow = 1, color = new Color(1f, 0.55f, 0.4f) }
            };
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = TextAnchor.MiddleLeft;
            header.Add(title);

            var clear = new Button(() =>
            {
                _loose.Clear();
                DrawWarnings();
            }) { text = "Dismiss" };
            clear.style.fontSize = 9;
            header.Add(clear);

            _warnings.Add(header);

            foreach (var loose in _loose)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 1;
                row.style.paddingLeft = 4;
                row.style.backgroundColor = new Color(0.35f, 0.15f, 0.1f, 0.6f);

                var text = new Label(loose.Node + "  ·  " + loose.Detail)
                {
                    style = { fontSize = 10, flexGrow = 1, color = new Color(0.95f, 0.85f, 0.8f) }
                };
                text.style.unityTextAlign = TextAnchor.MiddleLeft;
                text.tooltip = "This edge could not be rebuilt on the new node. The node at the " +
                               "other end is still there, now unconnected.";
                row.Add(text);

                var find = new Button(() => GraphNavigation.Focus(_window.Canvas, loose.Tab, loose.NodeId))
                {
                    text = "Find"
                };
                find.style.fontSize = 9;
                row.Add(find);

                _warnings.Add(row);
            }
        }

        private VisualElement Row(NodeUpgrade upgrade)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.paddingLeft = 4;
            row.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);

            var loses = upgrade.CanUpgrade && !upgrade.IsClean;

            // Three states worth telling apart at a glance: swaps cleanly, swaps but loses
            // something, and has nowhere to go at all.
            var colour = !upgrade.CanUpgrade
                ? new Color(0.6f, 0.6f, 0.6f)
                : loses
                    ? new Color(1f, 0.8f, 0.4f)
                    : new Color(0.95f, 0.95f, 0.95f);

            // A tilde on the ones whose replacement was worked out rather than declared. Small,
            // because it is a caveat rather than a warning — the port and value check that decides
            // whether a row is clean runs the same either way.
            var marker = upgrade.CanUpgrade && !upgrade.Declared ? "~ " : string.Empty;

            var name = new Label(marker + upgrade.Name)
            {
                style = { fontSize = 11, flexGrow = 1, color = colour }
            };
            name.style.unityTextAlign = TextAnchor.MiddleLeft;
            name.tooltip = Explain(upgrade);
            row.Add(name);

            var jump = new Button(() => Frame(upgrade)) { text = "Find" };
            jump.style.fontSize = 10;
            row.Add(jump);

            var upgradeButton = new Button(() => Upgrade(upgrade)) { text = "Upgrade" };
            upgradeButton.style.fontSize = 10;
            upgradeButton.SetEnabled(upgrade.CanUpgrade);
            upgradeButton.tooltip = Explain(upgrade);
            row.Add(upgradeButton);

            return row;
        }

        /// <summary>
        /// What the swap would do to this node, in the tooltip rather than the row: it is the
        /// detail you want before pressing, not while scanning the list.
        /// </summary>
        private static string Explain(NodeUpgrade upgrade)
        {
            if (!upgrade.CanUpgrade)
                return string.IsNullOrEmpty(upgrade.Message)
                    ? "Deprecated, with no replacement named. It has to be rebuilt by hand."
                    : upgrade.Message + "\n\nNo replacement named, so this has to be rebuilt by hand.";

            var text = "Replace with " + upgrade.Replacement.Name +
                       (upgrade.Declared
                           ? ", which this node names as its replacement."
                           : ". Nothing declares this — it is the newest node of the same name, so " +
                             "check it is the one you meant.");

            if (upgrade.DroppedPorts.Count > 0)
                text += "\n\nThese connections would be lost — the new node has no such port:\n" +
                        string.Join(", ", upgrade.DroppedPorts);

            if (upgrade.DroppedFields.Count > 0)
                text += "\n\nThese values would not carry across:\n" + string.Join(", ", upgrade.DroppedFields);

            if (upgrade.IsClean) text += "\n\nEverything carries across.";

            return text;
        }

        private void Upgrade(NodeUpgrade upgrade)
        {
            var graph = _window == null ? null : _window.Graph;
            if (graph == null) return;

            Undo.RecordObject(graph, "Upgrade " + upgrade.Name);

            var result = DeprecatedNodeUpgrader.Apply(graph, upgrade);
            if (result == null) return;

            Record(result, graph);
            AfterUpgrade(result.Replacement);
        }

        /// <summary>
        /// Keeps what a swap cut, in the panel and in the console. The console entry is the one
        /// that survives closing the window, and it pings the graph when clicked.
        /// </summary>
        private void Record(UpgradeResult result, Graph graph)
        {
            if (result.Dropped.Count == 0) return;

            var node = result.Replacement;

            foreach (var dropped in result.Dropped)
                _loose.Add(new Loose
                {
                    Node = node.Name,
                    NodeId = node.ID,
                    Tab = node.TabIndex,
                    Detail = dropped.ToString(),
                });

            Debug.LogWarning($"[Deprecated] {node.Name} was upgraded, but {result.Dropped.Count} " +
                             (result.Dropped.Count == 1 ? "connection" : "connections") +
                             " could not be rebuilt on the new node: " +
                             string.Join(", ", result.Dropped) + ".", graph);
        }

        /// <summary>
        /// Only the ones that lose nothing. Anything that would drop a connection or a value stays
        /// behind for its own button, where the tooltip has already said what it costs — a batch is
        /// no place to agree to that without reading it.
        /// </summary>
        private void UpgradeClean()
        {
            var graph = _window == null ? null : _window.Graph;
            if (graph == null) return;

            // One snapshot for the whole batch: it is the same single action to undo, and taking
            // one per node is what made a batch of forty take as long as it did.
            Undo.RecordObject(graph, "Upgrade deprecated nodes");

            var applied = 0;
            foreach (var upgrade in _found)
            {
                if (!upgrade.IsClean) continue;

                var result = DeprecatedNodeUpgrader.Apply(graph, upgrade);
                if (result == null) continue;

                // Clean ones are not supposed to lose anything; if one does, the prediction was
                // wrong and that is the more important thing to hear about.
                Record(result, graph);
                applied++;
            }

            if (applied == 0) return;

            Debug.Log($"[Deprecated] Upgraded {applied} node" + (applied == 1 ? "." : "s."));
            AfterUpgrade(null);
        }

        /// <summary>
        /// The canvas is showing views of nodes that are no longer in the graph, so it is rebuilt
        /// rather than patched — and the list with it, since every remaining entry now holds a
        /// node reference from before the edit.
        ///
        /// Rebuilding the canvas throws away the one thing you want after a swap: the node you
        /// just changed, where you left it. Reload frames the whole graph, so the pan and zoom are
        /// put back afterwards and the replacement is selected rather than framed — framing would
        /// move the view to it, which is the same jump by another name.
        ///
        /// Restored on the layout pass, not straight after Reload: Reload's own framing is
        /// deferred to that pass, so anything done here and now is overwritten a frame later.
        /// Queued second, it runs second.
        /// </summary>
        private void AfterUpgrade(Node replacement)
        {
            var canvas = _window == null ? null : _window.Canvas;
            if (canvas == null)
            {
                Rebuild();
                return;
            }

            var position = canvas.viewTransform.position;
            var scale = canvas.viewTransform.scale;

            canvas.Reload();

            canvas.ExecuteWhenLayoutReady(() =>
            {
                canvas.UpdateViewTransform(position, scale);

                if (replacement == null) return;

                // The views only exist once the canvas has been rebuilt, so the selection waits
                // here too rather than being made against the old ones.
                var view = canvas.FindNodeById(replacement.ID);
                if (view == null) return;

                canvas.ClearSelection();
                canvas.AddToSelection(view);
            });

            Rebuild();
        }

        private void Frame(NodeUpgrade upgrade)
        {
            var canvas = _window == null ? null : _window.Canvas;
            if (canvas == null || upgrade.Node == null) return;

            GraphNavigation.Focus(canvas, upgrade.Node.TabIndex, upgrade.Node.ID);
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
