using System.Collections.Generic;
using BlueGraph.Editor;

namespace Utilities.Editor
{
    /// <summary>
    /// Puts the view on a node, from anywhere.
    ///
    /// Switching tab is not a cheap move: it sets the tab index, which the canvas answers by
    /// reloading every element and then framing the whole graph on the next layout pass. Anything
    /// selected before that lands is selected on views about to be destroyed, and anything framed
    /// is framed before the graph is framed over it — which reads as a jump that loses the node
    /// you asked for.
    ///
    /// So the work waits for the layout, queued behind the canvas's own framing.
    /// </summary>
    public static class GraphNavigation
    {
        public static void Focus(CanvasView canvas, byte tab, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            Focus(canvas, tab, new[] { nodeId });
        }

        /// <summary>
        /// Selects these nodes and frames them, on the given tab. Ids that have no view on that tab
        /// are skipped rather than emptying the selection.
        /// </summary>
        public static void Focus(CanvasView canvas, byte tab, IReadOnlyList<string> nodeIds)
        {
            if (canvas == null || nodeIds == null || nodeIds.Count == 0) return;

            canvas.SwitchToTab(tab);

            canvas.ExecuteWhenLayoutReady(() =>
            {
                var found = 0;

                foreach (var id in nodeIds)
                {
                    var view = canvas.FindNodeById(id);
                    if (view == null) continue;

                    // Cleared on the first hit, so a miss leaves whatever was selected alone.
                    if (found == 0) canvas.ClearSelection();

                    canvas.AddToSelection(view);
                    found++;
                }

                if (found > 0) canvas.FrameSelection();
            });
        }
    }
}
