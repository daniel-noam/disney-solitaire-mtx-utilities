using System.Collections.Generic;
using BlueGraph;
using UnityEditor;

namespace Utilities.Editor
{
    /// <summary>
    /// The repairs behind the cleanup panel.
    ///
    /// Every one of these works out what to do from the graph as it is now, rather than from the
    /// report the panel is showing. The report can be a minute old — long enough to have dragged a
    /// node into a group since — and a sweep that trusted it would take that node straight back
    /// out again. What the report is for is telling somebody what a button will cost; what the
    /// button does is decided here.
    ///
    /// Records no undo of its own: <c>Undo.RecordObject</c> snapshots the whole graph asset, so
    /// the caller takes one for the button the person actually pressed rather than one per group.
    ///
    /// Nothing here adds or removes a group. Group colours live in a separate asset, keyed by the
    /// group's position in the list, so dropping one from the middle silently recolours every
    /// group after it — a group left empty is reported instead, for the editor's own right-click
    /// Delete Group, which keeps the two in step.
    /// </summary>
    public static class GraphCleaner
    {
        /// <summary>Ids removed from groups: the ones that resolve to nothing, and the repeats.</summary>
        public static int CleanGroups(Graph graph)
        {
            var groups = GraphInternals.GroupsOf(graph);
            if (groups == null) return 0;

            var live = LiveIds(graph);
            var removed = 0;

            foreach (var group in groups)
            {
                if (group?.nodes == null || group.nodes.Count == 0) continue;

                // Rebuilt from the ids that survived, in the order they were in: a group's list is
                // the only place that order is kept, and it is what the canvas walks.
                var kept = new List<string>(group.nodes.Count);
                var seen = new HashSet<string>();

                foreach (var id in group.nodes)
                {
                    if (string.IsNullOrEmpty(id) || seen.Add(id) == false) continue;
                    if (live.Contains(id)) kept.Add(id);
                }

                if (kept.Count == group.nodes.Count) continue;

                removed += group.nodes.Count - kept.Count;
                group.nodes = kept;
            }

            if (removed > 0) Commit(graph);

            return removed;
        }

        /// <summary>
        /// Brings groups stranded on a tab that no longer exists onto the last tab there is, where
        /// they can be seen and dealt with. Moved rather than deleted: an off-tab group is usually
        /// a tab removed from under it, and what is written in it is worth reading before it goes.
        /// </summary>
        public static int MoveOffTabGroups(Graph graph)
        {
            var groups = GraphInternals.GroupsOf(graph);
            if (groups == null) return 0;

            var tabs = graph.TabCount;
            if (tabs <= 0) return 0;

            var last = (byte) (tabs - 1);
            var moved = 0;

            foreach (var group in groups)
            {
                if (group == null || group.tabIndex < tabs) continue;

                group.tabIndex = last;
                moved++;
            }

            if (moved > 0) Commit(graph);

            return moved;
        }

        public static int RemoveEmptyComments(Graph graph)
        {
            var comments = GraphInternals.CommentsOf(graph);
            if (comments == null) return 0;

            var removed = comments.RemoveAll(GraphCleanupScanner.IsEmpty);

            if (removed > 0) Commit(graph);

            return removed;
        }

        /// <summary>
        /// Deletes the nodes nothing is wired to, and takes their ids out of any group that still
        /// lists them — otherwise this would hand the group list the very problem the rest of this
        /// file exists to clear.
        ///
        /// Each one is checked again here: the list came from a scan, and anything wired up since
        /// is no longer an orphan.
        /// </summary>
        public static int DeleteUnconnected(Graph graph, IReadOnlyList<Node> candidates)
        {
            if (graph == null || candidates == null || candidates.Count == 0) return 0;

            // Graph.Nodes rebuilds a filtered copy on every call, so the ids are taken once here
            // rather than once per candidate.
            var live = LiveIds(graph);
            var ids = new HashSet<string>();

            foreach (var node in candidates)
            {
                if (GraphCleanupScanner.IsUnconnected(node) == false) continue;
                if (live.Contains(node.ID) == false) continue;

                ids.Add(node.ID);
            }

            if (ids.Count == 0) return 0;

            var removed = 0;

            foreach (var node in candidates)
            {
                if (ids.Contains(node.ID) == false) continue;

                graph.RemoveNode(node);
                removed++;
            }

            var groups = GraphInternals.GroupsOf(graph);
            if (groups != null)
                foreach (var group in groups)
                    group?.nodes?.RemoveAll(id => ids.Contains(id));

            Commit(graph);

            return removed;
        }

        private static HashSet<string> LiveIds(Graph graph)
        {
            var live = new HashSet<string>();

            foreach (var node in graph.Nodes)
                if (node != null) live.Add(node.ID);

            return live;
        }

        private static void Commit(Graph graph)
        {
            EditorUtility.SetDirty(graph);
            AssetDatabase.SaveAssetIfDirty(graph);
        }
    }
}
