using System;
using System.Collections.Generic;
using BlueGraph;
using BlueGraph.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Utilities.Editor
{
    /// <summary>
    /// What the graph is still carrying from work already undone, and the buttons to be rid of it.
    ///
    /// Deleting a node that sits in a group leaves its id in that group's list, where nothing will
    /// ever show it to you again. It is not a fault — the canvas skips an id it cannot resolve —
    /// but it accumulates, it is walked against every drawn element each time the graph loads, and
    /// it turns the asset into pages of dead guids that make a diff on the graph unreadable.
    ///
    /// The counts are the point. Each says what a press would cost before it is pressed, which is
    /// what makes a confirmation dialog unnecessary.
    /// </summary>
    public class GraphCleanupPanel : VisualElement
    {
        private static readonly Color Dim = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color Muted = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color Warn = new Color(1f, 0.8f, 0.4f);
        private static readonly Color Text = new Color(0.95f, 0.95f, 0.95f);

        private readonly GraphEditorWindow _window;
        private readonly Label _headline;
        private readonly ScrollView _rows;
        private readonly VisualElement _actions;

        private CleanupReport _report = new CleanupReport();

        /// <summary>
        /// Where each row's Find got to, so pressing it again goes to the next one rather than
        /// back to the same group. Keyed by row, and reset whenever the list is rebuilt under it.
        /// </summary>
        private readonly Dictionary<string, int> _cursor = new Dictionary<string, int>();

        public GraphCleanupPanel(GraphEditorWindow window)
        {
            _window = window;

            style.width = 320;
            style.maxHeight = 380;
            style.paddingTop = 8;
            style.paddingBottom = 8;
            style.paddingLeft = 8;
            style.paddingRight = 8;

            style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.92f);
            SetBorder(1, new Color(1f, 1f, 1f, 0.35f));

            Add(new Label("Cleanup") { style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold } });

            _headline = new Label { style = { fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            _headline.style.color = Dim;
            Add(_headline);

            _rows = new ScrollView { style = { flexGrow = 1 } };
            Add(_rows);

            _actions = new VisualElement { style = { marginTop = 4 } };
            Add(_actions);

            var refresh = new Button(Rebuild) { text = "Refresh" };
            refresh.style.fontSize = 10;
            Add(refresh);
        }

        public void Rebuild()
        {
            _rows.Clear();
            _actions.Clear();
            _cursor.Clear();

            _report = GraphCleanupScanner.Scan(_window == null ? null : _window.Graph);

            _headline.text = _report.Anything
                ? "None of this changes what the graph does. It is what is left over."
                : "Nothing left over in this graph.";

            Rows();
            Actions();
        }

        private void Rows()
        {
            if (_report.DeadIds > 0)
                Row("dead",
                    _report.DeadIds + (_report.DeadIds == 1 ? " node id" : " node ids") + " in " +
                    _report.GroupsWithWork + (_report.GroupsWithWork == 1 ? " group" : " groups") +
                    " point at nothing",
                    "The nodes were deleted; the groups they were in still list them. Cleaning " +
                    "these changes nothing you can see, which is why they pile up.",
                    Warn, GroupsWith(issue => issue.Dead.Count > 0));

            if (_report.DuplicateIds > 0)
                Row("dupes",
                    _report.DuplicateIds + (_report.DuplicateIds == 1 ? " id is" : " ids are") +
                    " listed twice in the same group",
                    "A node counted more than once by its group. Cleaning keeps the first.",
                    Warn, GroupsWith(issue => issue.Duplicates > 0));

            if (_report.EmptyGroups > 0)
                Row("empty",
                    _report.EmptyGroups + (_report.EmptyGroups == 1 ? " group has" : " groups have") +
                    " nothing left in them",
                    "Nothing is deleted for you here. Group colours are kept in a separate asset, " +
                    "keyed by each group's place in the list, so removing one from the middle " +
                    "would recolour every group after it. Right-click the group and use the " +
                    "editor's own Delete Group, which keeps the two in step.",
                    Muted, GroupsWith(issue => issue.EmptyAfterClean));

            if (_report.OffTabGroups > 0)
                Row("offtab",
                    _report.OffTabGroups + (_report.OffTabGroups == 1 ? " group sits" : " groups sit") +
                    " on a tab that is gone",
                    "The canvas only draws groups belonging to the tab it is showing, so these " +
                    "cannot be seen or selected at all. The button below brings them onto the last " +
                    "tab, where you can read them and decide.",
                    Warn, null);

            if (_report.EmptyComments.Count > 0)
                Row("comments",
                    _report.EmptyComments.Count + (_report.EmptyComments.Count == 1 ? " comment has" : " comments have") +
                    " nothing written in them",
                    "An empty comment box. Nothing indexes comments, so these are simply removed.",
                    Muted, null);

            if (_report.Unconnected.Count > 0)
                Row("unconnected",
                    _report.Unconnected.Count + (_report.Unconnected.Count == 1 ? " node has" : " nodes have") +
                    " nothing connected",
                    "Nothing runs them and they run nothing — but that is also what a flow you are " +
                    "halfway through building looks like, so they are never part of the sweep. " +
                    "Look through them with Find first. A subgraph's own Input and Output nodes " +
                    "are left out of this count: unconnected is how they are supposed to look.",
                    Muted, null, () => Focus(_report.Unconnected));

            if (_report.Anything == false)
                _rows.Add(new Label("Groups, comments and nodes all account for themselves.")
                {
                    style = { fontSize = 10, color = Muted, marginTop = 2, whiteSpace = WhiteSpace.Normal }
                });
        }

        private List<Group> GroupsWith(Func<GroupIssue, bool> predicate)
        {
            var groups = new List<Group>();

            foreach (var issue in _report.Groups)
                if (predicate(issue) && issue.Group != null) groups.Add(issue.Group);

            return groups;
        }

        /// <summary>
        /// One finding. Find steps through the places it was found rather than jumping to the
        /// first every time — with eighty-seven groups to look at, going back to the same one is
        /// the same as having no button at all.
        /// </summary>
        private void Row(string key, string headline, string explain, Color colour,
            List<Group> groups, Action find = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.paddingLeft = 4;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;
            row.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);

            var label = new Label(headline)
            {
                style = { fontSize = 10, flexGrow = 1, color = colour, whiteSpace = WhiteSpace.Normal }
            };
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.tooltip = explain;
            row.Add(label);

            if (find == null && groups != null && groups.Count > 0)
                find = () => Step(key, groups);

            if (find != null)
            {
                var button = new Button(find) { text = "Find" };
                button.style.fontSize = 10;
                button.tooltip = groups != null && groups.Count > 1
                    ? "Steps through the " + groups.Count + " groups, one press at a time."
                    : explain;
                row.Add(button);
            }

            _rows.Add(row);
        }

        private void Step(string key, List<Group> groups)
        {
            var canvas = _window == null ? null : _window.Canvas;
            if (canvas == null || groups.Count == 0) return;

            var next = _cursor.TryGetValue(key, out var at) ? at : 0;
            if (next >= groups.Count) next = 0;

            _cursor[key] = next + 1;

            GraphNavigation.Focus(canvas, groups[next]);
        }

        private void Focus(List<Node> nodes)
        {
            var canvas = _window == null ? null : _window.Canvas;
            if (canvas == null || nodes.Count == 0) return;

            var next = _cursor.TryGetValue("nodes", out var at) ? at : 0;
            if (next >= nodes.Count) next = 0;

            _cursor["nodes"] = next + 1;

            var node = nodes[next];
            if (node != null) GraphNavigation.Focus(canvas, node.TabIndex, node.ID);
        }

        /// <summary>
        /// The sweep is one button because the three things in it are the same decision: they are
        /// records of nodes and text that are already gone. Moving groups between tabs and
        /// deleting nodes are not, so they stay separate and say what they will do.
        /// </summary>
        private void Actions()
        {
            if (_report.Sweepable > 0)
                Action("Clean up " + _report.Sweepable + (_report.Sweepable == 1 ? " leftover" : " leftovers"),
                    "Dead and repeated ids out of their groups, and empty comments. No group, " +
                    "node or wire is touched.",
                    true, Sweep);

            if (_report.OffTabGroups > 0)
                Action("Move " + _report.OffTabGroups + (_report.OffTabGroups == 1 ? " group" : " groups") +
                       " onto the last tab",
                    "Brings groups stranded on a tab that no longer exists back where they can be seen.",
                    false, MoveOffTab);

            if (_report.Unconnected.Count > 0)
                Action("Delete " + _report.Unconnected.Count +
                       (_report.Unconnected.Count == 1 ? " unconnected node" : " unconnected nodes"),
                    "Deletes them and takes their ids out of any group. Undo puts them back, but " +
                    "look through them with Find first.",
                    false, DeleteUnconnected);
        }

        private void Action(string text, string tooltip, bool primary, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            button.style.fontSize = 10;
            button.style.height = primary ? 22 : 20;
            button.style.marginBottom = 2;

            if (primary) button.style.unityFontStyleAndWeight = FontStyle.Bold;

            _actions.Add(button);
        }

        private void Sweep()
        {
            var graph = _window == null ? null : _window.Graph;
            if (graph == null) return;

            // One snapshot for the whole sweep. RecordObject copies the entire graph asset, so a
            // snapshot per group would be both slow and wrong — it is one action to undo.
            Undo.RegisterCompleteObjectUndo(graph, "Clean up graph");

            var ids = GraphCleaner.CleanGroups(graph);
            var comments = GraphCleaner.RemoveEmptyComments(graph);

            if (ids + comments == 0)
            {
                Rebuild();
                return;
            }

            Debug.Log($"[Cleanup] Removed {ids} dead or repeated node " + (ids == 1 ? "id" : "ids") +
                      " from groups" + (comments > 0 ? $" and {comments} empty comment" + (comments == 1 ? "." : "s.") : "."),
                      graph);

            Refresh();
        }

        private void MoveOffTab()
        {
            var graph = _window == null ? null : _window.Graph;
            if (graph == null) return;

            Undo.RegisterCompleteObjectUndo(graph, "Move groups onto a tab");

            var moved = GraphCleaner.MoveOffTabGroups(graph);
            if (moved == 0)
            {
                Rebuild();
                return;
            }

            Debug.Log($"[Cleanup] Moved {moved} " + (moved == 1 ? "group" : "groups") +
                      " onto the last tab.", graph);

            Refresh();
        }

        private void DeleteUnconnected()
        {
            var graph = _window == null ? null : _window.Graph;
            if (graph == null) return;

            Undo.RegisterCompleteObjectUndo(graph, "Delete unconnected nodes");

            var deleted = GraphCleaner.DeleteUnconnected(graph, _report.Unconnected);
            if (deleted == 0)
            {
                Rebuild();
                return;
            }

            Debug.Log($"[Cleanup] Deleted {deleted} unconnected " + (deleted == 1 ? "node." : "nodes."), graph);

            Refresh();
        }

        /// <summary>
        /// The canvas is showing views of groups and nodes that have just changed underneath it,
        /// so it is rebuilt rather than repainted. Reload frames the whole graph, which is a jump
        /// away from wherever you were working, so the pan and zoom go back on the layout pass —
        /// behind Reload's own framing, which is deferred to that same pass and would otherwise
        /// overwrite anything done here and now.
        /// </summary>
        private void Refresh()
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
            canvas.ExecuteWhenLayoutReady(() => canvas.UpdateViewTransform(position, scale));

            Rebuild();
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
