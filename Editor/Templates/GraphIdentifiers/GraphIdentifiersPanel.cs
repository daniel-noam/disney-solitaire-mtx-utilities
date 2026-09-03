using System.Collections.Generic;
using BlueGraph;
using BlueGraph.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Utilities.Editor
{
    /// <summary>
    /// The method ids and trigger names in the open graph, each renamed across every node that
    /// uses it at once.
    ///
    /// Doing this by hand means finding each node and retyping the same string, and the failure is
    /// silent: a CallMethod pointing at a name no OnMethod answers to is not an error, it is a flow
    /// that stops. So the list is as much of the tool as the rename — seeing every name in the
    /// graph, with what uses it, is what tells you a name is wrong in the first place.
    /// </summary>
    public class GraphIdentifiersPanel : VisualElement
    {
        private static readonly Color Dim = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color Muted = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color Warn = new Color(1f, 0.8f, 0.4f);
        private static readonly Color Text = new Color(0.95f, 0.95f, 0.95f);

        private readonly GraphEditorWindow _window;
        private readonly Label _headline;
        private readonly ScrollView _rows;
        private readonly Label _footnote;

        private IdentifierScan _scan = new IdentifierScan();

        /// <summary>
        /// The one row showing a text field instead of its name. Held as the value rather than the
        /// group, because a rescan builds new groups and the row being edited has to survive it.
        /// </summary>
        private string _editing;

        private IdentifierKind _editingKind;

        public GraphIdentifiersPanel(GraphEditorWindow window)
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

            Add(new Label("Rename") { style = { fontSize = 14, unityFontStyleAndWeight = FontStyle.Bold } });

            _headline = new Label { style = { fontSize = 10, whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            _headline.style.color = Dim;
            Add(_headline);

            _rows = new ScrollView { style = { flexGrow = 1 } };
            Add(_rows);

            _footnote = new Label { style = { fontSize = 9, whiteSpace = WhiteSpace.Normal, marginTop = 4 } };
            _footnote.style.color = Muted;
            Add(_footnote);

            var refresh = new Button(Rebuild) { text = "Refresh" };
            refresh.style.fontSize = 10;
            refresh.style.marginTop = 4;
            Add(refresh);
        }

        public void Rebuild()
        {
            _rows.Clear();

            _scan = GraphIdentifierScanner.Scan(_window == null ? null : _window.Graph);

            _headline.text = _scan.Methods.Count + " method" + (_scan.Methods.Count == 1 ? "" : "s") +
                             "  ·  " + _scan.Triggers.Count + " trigger" + (_scan.Triggers.Count == 1 ? "" : "s") +
                             "\nRenaming one moves every node in this graph that uses it.";

            Section("Methods", _scan.Methods);
            Section("Triggers", _scan.Triggers);

            if (_scan.Methods.Count == 0 && _scan.Triggers.Count == 0)
                _rows.Add(new Label("No method or trigger names in this graph.")
                {
                    style = { fontSize = 10, color = Muted, marginTop = 2 }
                });

            Footnote();
        }

        private void Section(string title, IReadOnlyList<IdentifierGroup> groups)
        {
            if (groups.Count == 0) return;

            var header = new Label(title)
            {
                style = { fontSize = 10, color = Dim, marginTop = 4, unityFontStyleAndWeight = FontStyle.Bold }
            };
            _rows.Add(header);

            foreach (var group in groups) _rows.Add(Row(group));
        }

        private VisualElement Row(IdentifierGroup group)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.paddingLeft = 4;
            row.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);

            if (_editing == group.Value && _editingKind == group.Kind && group.CanRename)
            {
                FillEditing(row, group);
                return row;
            }

            var problem = Problem(group);

            var name = new Label(Display(group))
            {
                style =
                {
                    fontSize = 11,
                    flexGrow = 1,
                    color = group.CanRename == false ? Muted : problem == null ? Text : Warn,
                }
            };
            name.style.unityTextAlign = TextAnchor.MiddleLeft;
            name.tooltip = Explain(group, problem);
            row.Add(name);

            var summary = new Label(group.Summary()) { style = { fontSize = 9, color = Muted } };
            summary.style.unityTextAlign = TextAnchor.MiddleLeft;
            summary.style.marginRight = 4;
            summary.tooltip = Explain(group, problem);
            row.Add(summary);

            var find = new Button(() => Frame(group)) { text = "Find" };
            find.style.fontSize = 10;
            row.Add(find);

            var rename = new Button(() =>
            {
                _editing = group.Value;
                _editingKind = group.Kind;
                Rebuild();
            }) { text = "Rename" };
            rename.style.fontSize = 10;
            rename.SetEnabled(group.CanRename);
            rename.tooltip = group.CanRename
                ? "Rename this on every node in this graph that uses it."
                : ReadOnlyReason(group);
            row.Add(rename);

            return row;
        }

        /// <summary>
        /// The row mid-rename. Enter commits and Escape backs out, so the keyboard alone does it —
        /// and the buttons are there anyway, because nothing on screen says which keys work.
        /// </summary>
        private void FillEditing(VisualElement row, IdentifierGroup group)
        {
            var field = new TextField { value = group.Value };
            field.style.flexGrow = 1;
            field.style.fontSize = 11;
            field.style.marginRight = 2;

            var clash = new Label { style = { fontSize = 9, color = Warn, marginRight = 4 } };
            clash.style.unityTextAlign = TextAnchor.MiddleLeft;

            void CheckClash(string typed)
            {
                var merging = Existing(group.Kind, typed) && typed != group.Value;

                clash.text = merging ? "merges" : string.Empty;
                clash.tooltip = merging
                    ? "A name already used in this graph. Renaming onto it joins the two — " +
                      "which is sometimes the point, and is undoable either way."
                    : string.Empty;
            }

            CheckClash(group.Value);
            field.RegisterValueChangedCallback(evt => CheckClash(evt.newValue));

            // Both endings rebuild the list, which takes this field off screen. Doing that from
            // inside the field's own key event is pulling the rug from under the element handling
            // it, so the rebuild is queued for the next frame instead — the buttons can call
            // straight through, because a click is over by the time the handler runs.
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    var typed = field.value;
                    schedule.Execute(() => Apply(group, typed));
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    schedule.Execute(Cancel);
                    evt.StopPropagation();
                }
            });

            row.Add(field);
            row.Add(clash);

            var apply = new Button(() => Apply(group, field.value)) { text = "Apply" };
            apply.style.fontSize = 10;
            row.Add(apply);

            var cancel = new Button(Cancel) { text = "Cancel" };
            cancel.style.fontSize = 10;
            row.Add(cancel);

            // The field only exists from this frame on, so focusing it has to wait for the panel
            // to have laid out — asked for now, it goes to an element with no place on screen yet.
            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectAll();
            });
        }

        private void Cancel()
        {
            _editing = null;
            Rebuild();
        }

        private void Apply(IdentifierGroup group, string typed)
        {
            var graph = _window == null ? null : _window.Graph;
            var value = typed == null ? string.Empty : typed.Trim();

            if (graph == null || string.IsNullOrEmpty(value) || value == group.Value)
            {
                Cancel();
                return;
            }

            var renamed = GraphIdentifierRefactorer.Rename(graph, group, value);

            _editing = null;

            if (renamed == 0)
            {
                Rebuild();
                return;
            }

            Debug.Log($"[Rename] {group.Value} → {value} on {renamed} node" +
                      (renamed == 1 ? "." : "s."), graph);

            Refresh();
        }

        /// <summary>
        /// The node views hold their own copy of what they draw, so the canvas is rebuilt rather
        /// than asked to repaint — otherwise the rows here say one name and the nodes another
        /// until something else happens to reload them.
        ///
        /// Reload frames the whole graph, which after a rename is a jump away from wherever you
        /// were working. The pan and zoom are put back on the layout pass, behind Reload's own
        /// framing, so they are restored after it rather than overwritten by it.
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

        private void Frame(IdentifierGroup group)
        {
            var canvas = _window == null ? null : _window.Canvas;
            if (canvas == null || group.Uses.Count == 0) return;

            // A group can be spread over several tabs, and only one can be shown. The first use's
            // tab is the one taken, and Focus skips the ids that have no view on it.
            var tab = group.Uses[0].Node.TabIndex;

            var ids = new List<string>(group.Uses.Count);
            foreach (var use in group.Uses)
                if (use.Node != null) ids.Add(use.Node.ID);

            GraphNavigation.Focus(canvas, tab, ids);
        }

        private bool Existing(IdentifierKind kind, string value)
        {
            var groups = kind == IdentifierKind.Method ? _scan.Methods : _scan.Triggers;

            foreach (var group in groups)
                if (group.Value == value) return true;

            return false;
        }

        private static string Display(IdentifierGroup group) =>
            string.IsNullOrEmpty(group.Value) ? "(not set)" : group.Value;

        /// <summary>
        /// What is wrong with this name, in one line, or null when nothing is. Only the joins this
        /// graph can answer for: a trigger nothing here sets may well be set by the game, and
        /// saying otherwise would be crying wolf on the ordinary case.
        /// </summary>
        private static string Problem(IdentifierGroup group)
        {
            if (string.IsNullOrEmpty(group.Value))
                return "These nodes have no name typed in, so nothing joins them to anything.";

            if (group.Kind == IdentifierKind.Method)
            {
                if (group.HasRole("defines") == false)
                    return "Nothing in this graph defines this method, so these calls run nothing.";

                if (group.HasRole("calls") == false)
                    return "Defined, but nothing calls it.";

                return null;
            }

            if (group.HasRole("sets") == false && group.HasRole("sets (fixed)") == false)
                return "Nothing in this graph sets this trigger. That is fine if the game or " +
                       "another template sets it — worth checking that it does.";

            return null;
        }

        private static string ReadOnlyReason(IdentifierGroup group)
        {
            if (string.IsNullOrEmpty(group.Value))
                return "There is no name here to rename. Type one on the nodes themselves.";

            var blocked = group.FirstBlocked();
            if (blocked == null) return string.Empty;

            if (blocked.Field == null)
                return "One of these takes this name from code rather than a field — the fixed " +
                       "trigger nodes do. Renaming the rest would unwire them from it.";

            return "The String node feeding " + blocked.Node.Name + " also feeds something that is " +
                   "not part of this name, so renaming it would change that too. Give that node its " +
                   "own String node, or rename it by hand.";
        }

        private static string Explain(IdentifierGroup group, string problem)
        {
            var text = Display(group) + "\n" + group.Summary();

            var constants = group.ViaConstants();
            if (constants > 0)
                text += "\n\n" + constants + " of these take the name from a String node wired into " +
                        "the port. Those String nodes are renamed along with the rest.";

            if (group.HasRole("from a component"))
                text += "\n\nOne of these listens for a trigger a prefab component sends. The name " +
                        "is typed on that component as well, and renaming here does not change it.";

            if (problem != null) text += "\n\n" + problem;

            if (group.CanRename == false) text += "\n\n" + ReadOnlyReason(group);

            return text;
        }

        private void Footnote()
        {
            var lines = new List<string>();

            if (_scan.EdgeDriven.Count > 0)
                lines.Add(_scan.EdgeDriven.Count + " node" + (_scan.EdgeDriven.Count == 1 ? " takes its" : "s take their") +
                          " name from a connection that is worked out at runtime. Those are not listed.");

            if (_scan.Subgraphs > 0)
                lines.Add("Subgraphs have names of their own — open one to rename inside it.");

            _footnote.text = string.Join("\n", lines);
            _footnote.style.display = lines.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
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
