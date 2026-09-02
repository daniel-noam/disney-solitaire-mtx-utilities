using System.Collections.Generic;
using System.Linq;
using BlueGraph.Editor;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace Utilities.Editor
{
    /// <summary>
    /// Takes you from a binding key to the nodes that use it.
    ///
    /// The inspector could always tell you a key was used three times and never which three. The
    /// analyzer walks those nodes to count them, so the only thing missing was somewhere to go.
    /// </summary>
    public static class BindingKeyNavigator
    {
        /// <summary>The USS classes the canvas puts on its own search controls.</summary>
        private const string SearchInputClass = "searchBoxInput";
        private const string SearchDropdownClass = "searchBoxDropdown";

        /// <summary>The search mode that looks inside node values rather than at node names.</summary>
        private const string ValuesMode = "Values";

        /// <summary>
        /// Opens the graph holding the most of these nodes and hands the key to the canvas's own
        /// search, in Values mode.
        ///
        /// Driving the search rather than selecting the nodes ourselves: references to one key are
        /// usually scattered, and the search box already has next/previous and a results list to
        /// walk them with. Selecting them all at once only ever showed the ones that happened to
        /// share a tab, and left you framed on a cloud of nodes with no way through it.
        ///
        /// Falls back to selecting them if the search controls cannot be found — they are located
        /// by the class names the canvas gives them, which is a contract nothing enforces.
        /// </summary>
        public static void Show(IReadOnlyList<BindingSite> sites, string key)
        {
            if (sites == null || sites.Count == 0) return;

            var graph = BusiestGraph(sites);
            if (graph == null) return;

            var window = GraphEditor.GetExistingEditorWindow(graph) ?? GraphEditor.CreateEditorWindow(graph);
            if (window == null) return;

            window.Focus();

            var canvas = window.Canvas;
            if (canvas == null) return;

            if (Search(canvas, key)) return;

            SelectNodes(canvas, sites, graph);
        }

        /// <summary>
        /// Types the key into the canvas's search and presses Return on its behalf. Returns false
        /// when the controls are not where they used to be, so the caller can do something else.
        /// </summary>
        private static bool Search(VisualElement canvas, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var input = canvas.Q<TextField>(className: SearchInputClass);
            if (input == null) return false;

            // Values, not Nodes: a binding key is a literal inside a node, not the node's name.
            var mode = canvas.Q<DropdownField>(className: SearchDropdownClass);
            if (mode != null && mode.choices != null && mode.choices.Contains(ValuesMode))
                mode.value = ValuesMode;

            input.value = key;

            // Return through the field rather than reaching for the private search method: it is
            // the same path a person's keystroke takes, so whatever the search keeps track of ends
            // up in the state it would be in if they had typed it.
            using (var stroke = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None))
            {
                stroke.target = input;
                input.SendEvent(stroke);
            }

            return true;
        }

        private static void SelectNodes(CanvasView canvas, IReadOnlyList<BindingSite> sites, TemplateBehavior graph)
        {

            // The tab of the first node in that graph decides which of them can be shown at once.
            byte tab = 0;
            var found = false;
            foreach (var site in sites)
            {
                if (site.Graph != graph || site.Node == null) continue;
                tab = site.Node.TabIndex;
                found = true;
                break;
            }

            if (!found) return;

            var ids = new List<string>();
            foreach (var site in sites)
                if (site.Graph == graph && site.Node != null && site.Node.TabIndex == tab)
                    ids.Add(site.Node.ID);

            GraphNavigation.Focus(canvas, tab, ids);
        }

        /// <summary>
        /// The graph with the most of these nodes in it. A key used in a subgraph and once in the
        /// template would otherwise open whichever happened to be scanned first.
        /// </summary>
        private static TemplateBehavior BusiestGraph(IReadOnlyList<BindingSite> sites)
        {
            var counts = new Dictionary<TemplateBehavior, int>();

            foreach (var site in sites)
            {
                if (site.Graph == null) continue;
                counts.TryGetValue(site.Graph, out var count);
                counts[site.Graph] = count + 1;
            }

            TemplateBehavior best = null;
            var most = 0;
            foreach (var pair in counts)
            {
                if (pair.Value <= most) continue;
                best = pair.Key;
                most = pair.Value;
            }

            return best;
        }

        /// <summary>How the menu entry reads, including where the nodes are when it is not obvious.</summary>
        public static string Describe(string key, IReadOnlyList<BindingSite> sites)
        {
            if (sites == null || sites.Count == 0) return null;

            // A "/" in a GenericMenu label silently turns the entry into a submenu, which would
            // hide it. Binding keys are not supposed to contain one, but the menu is not the place
            // to find that out.
            var safe = (key ?? string.Empty).Replace("/", "\u2215");

            var label = sites.Count == 1
                ? $"Show the node using \"{safe}\""
                : $"Show the {sites.Count} nodes using \"{safe}\"";

            // Named when every use is inside one subgraph, because that is when opening the graph
            // you are looking at would show you nothing.
            var path = sites[0].Path;
            if (string.IsNullOrEmpty(path)) return label;

            foreach (var site in sites)
                if (site.Path != path) return label;

            return label + " (in " + path + ")";
        }
    }
}
