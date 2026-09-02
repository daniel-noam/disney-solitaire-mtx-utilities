using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using BlueGraph.Editor;

namespace Utilities.Editor
{
    /// <summary>
    /// Puts this assembly's panels into every open graph editor, without the graph editor knowing.
    ///
    /// Additive on purpose: nothing in the graph editor is edited to make this work, so the tool
    /// travels with LinkedAssets and a project that has never seen it is unaffected. The cost is
    /// that the attachment is by inspection rather than by contract — it finds the toolbar by
    /// type and hangs the panel on the canvas — so it fails by quietly not appearing if that
    /// layout ever changes, rather than by failing to compile.
    ///
    /// Polled rather than event-driven because an EditorWindow has no "opened" event to hook, and
    /// the window rebuilds its whole visual tree in Load — on a domain reload, on entering play
    /// mode, on opening another graph. Re-checking is what makes it come back each time rather
    /// than needing to be reattached by hand.
    /// </summary>
    [InitializeOnLoad]
    public static class GraphToolPanelInjector
    {
        /// <summary>
        /// Every panel this assembly hangs on the graph editor: its toolbar label, and how to
        /// build and refresh it. Adding a tool here is the whole of attaching it.
        /// </summary>
        private static readonly (string Label, System.Func<GraphEditorWindow, VisualElement> Build,
            System.Action<VisualElement> Refresh)[] Panels =
        {
            ("Min Version",
                window => new TemplateVersionPanel(window),
                panel => ((TemplateVersionPanel) panel).Rebuild()),

            ("Deprecated",
                window => new DeprecatedNodesPanel(window),
                panel => ((DeprecatedNodesPanel) panel).Rebuild()),
        };

        /// <summary>
        /// The stretch that pushes injected toggles to the far end of the toolbar. Named and
        /// reused, so several tools attaching share one gap and end up grouped together rather
        /// than each carving up what is left of the row.
        /// </summary>
        private const string SpacerName = "injected-tools-spacer";

        /// <summary>Once a second: this is housekeeping, not something anybody is waiting on.</summary>
        private const double Interval = 1.0;

        private static double _next;

        static GraphToolPanelInjector()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + Interval;

            // Includes windows that are open but not focused, and the ones Unity has restored but
            // not yet shown. Attach returns immediately for any that already has the panel.
            foreach (var window in Resources.FindObjectsOfTypeAll<GraphEditorWindow>())
                Attach(window);
        }

        private static void Attach(GraphEditorWindow window)
        {
            if (window == null || window.Canvas == null) return;

            var root = window.rootVisualElement;
            if (root == null) return;

            var toolbar = root.Q<Toolbar>();
            if (toolbar == null) return;

            foreach (var definition in Panels)
            {
                // The toggle is the marker: finding one under this name means the window has
                // already been dealt with. Cheaper than tracking windows in a set, and correct
                // across the tree being rebuilt underneath us.
                var name = ToggleName(definition.Label);
                if (toolbar.Q<ToolbarToggle>(name) != null) continue;

                var panel = definition.Build(window);
                if (panel == null) continue;

                // Down the right, where the canvas keeps nothing of its own — the search box and
                // the groups dropdown are on the left, and Performance Stats is above.
                panel.style.alignSelf = Align.FlexEnd;
                panel.style.marginRight = 15;
                panel.style.marginTop = 10;
                panel.style.display = DisplayStyle.None;

                window.Canvas.Add(panel);

                var refresh = definition.Refresh;
                var toggle = new ToolbarToggle { text = definition.Label, name = name, value = false };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    panel.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                    if (evt.newValue) refresh(panel);
                });

                // On the right, like the panels they open, and away from the editor's own controls.
                var spacer = toolbar.Q<VisualElement>(SpacerName);
                if (spacer == null)
                {
                    spacer = new VisualElement { name = SpacerName };
                    spacer.style.flexGrow = 1;
                    toolbar.Add(spacer);
                }

                toolbar.Add(toggle);
            }
        }

        private static string ToggleName(string label) =>
            "injected-tool-" + label.ToLowerInvariant().Replace(' ', '-');
    }
}
