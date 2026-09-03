using System.Collections.Generic;
using BlueGraph;

namespace Utilities.Editor
{
    /// <summary>What one group is carrying that is no longer there.</summary>
    public sealed class GroupIssue
    {
        public Group Group { get; }

        /// <summary>Ids of nodes that no longer exist in the graph.</summary>
        public List<string> Dead { get; } = new List<string>();

        /// <summary>Ids listed more than once. Cleaning keeps the first of each.</summary>
        public int Duplicates { get; set; }

        /// <summary>Ids that still resolve, so the group has something to show for itself.</summary>
        public List<string> Live { get; } = new List<string>();

        /// <summary>
        /// True when the group sits on a tab the graph no longer has. The canvas only draws groups
        /// whose tab is the one being shown, so these cannot be seen, selected or deleted at all.
        /// </summary>
        public bool OffTab { get; set; }

        /// <summary>True when cleaning would leave the group with nothing in it.</summary>
        public bool EmptyAfterClean => Live.Count == 0;

        public bool HasWork => Dead.Count > 0 || Duplicates > 0;

        public GroupIssue(Group group)
        {
            Group = group;
        }
    }

    /// <summary>Everything the cleanup pass found, counted.</summary>
    public sealed class CleanupReport
    {
        public List<GroupIssue> Groups { get; } = new List<GroupIssue>();

        /// <summary>Comments with nothing typed in them.</summary>
        public List<Comment> EmptyComments { get; } = new List<Comment>();

        /// <summary>
        /// Nodes with nothing connected to any port. They run nothing and nothing runs them, but
        /// they are also what a half-built flow looks like, so these are never part of a batch.
        /// </summary>
        public List<Node> Unconnected { get; } = new List<Node>();

        public int DeadIds { get; set; }
        public int DuplicateIds { get; set; }
        public int GroupsWithWork { get; set; }
        public int EmptyGroups { get; set; }
        public int OffTabGroups { get; set; }

        /// <summary>What a single press of the clean button would deal with.</summary>
        public int Sweepable => DeadIds + DuplicateIds + EmptyComments.Count;

        public bool Anything => Sweepable > 0 || EmptyGroups > 0 || OffTabGroups > 0 || Unconnected.Count > 0;
    }

    /// <summary>
    /// What a graph is still carrying from work that has already been undone: node ids in groups
    /// whose nodes were deleted, above all.
    ///
    /// None of this changes what the graph does — a group is a box drawn round some nodes, and an
    /// id in it that resolves to nothing is skipped. It is worth clearing anyway, because the
    /// canvas walks every id in every group against every element it has drawn each time it
    /// loads, and because a graph whose asset is thousands of lines of dead guids makes every
    /// diff on it unreadable. Saying that plainly matters: a cleanup tool that implies a graph is
    /// broken when it is not gets used on the wrong things.
    /// </summary>
    public static class GraphCleanupScanner
    {
        /// <summary>Subgraph interface nodes, which are not orphans however unconnected they look.</summary>
        private static readonly string[] InterfaceNodes = { "InputNode", "OutputNode" };

        public static CleanupReport Scan(Graph graph)
        {
            var report = new CleanupReport();
            if (graph == null) return report;

            var live = new HashSet<string>();
            foreach (var node in graph.Nodes)
                if (node != null) live.Add(node.ID);

            ScanGroups(graph, live, report);
            ScanComments(graph, report);
            ScanUnconnected(graph, report);

            return report;
        }

        private static void ScanGroups(Graph graph, HashSet<string> live, CleanupReport report)
        {
            var groups = GraphInternals.GroupsOf(graph);
            if (groups == null) return;

            // TabCount is 0 on a graph that has never named a tab, and that graph still has the
            // one tab everything sits on — so nothing is off-tab until there are tabs to be off.
            var tabs = graph.TabCount;

            foreach (var group in groups)
            {
                if (group == null) continue;

                var issue = new GroupIssue(group);
                var seen = new HashSet<string>();

                if (group.nodes != null)
                {
                    foreach (var id in group.nodes)
                    {
                        if (string.IsNullOrEmpty(id)) continue;

                        if (seen.Add(id) == false)
                        {
                            issue.Duplicates++;
                            continue;
                        }

                        if (live.Contains(id)) issue.Live.Add(id);
                        else issue.Dead.Add(id);
                    }
                }

                issue.OffTab = tabs > 0 && group.tabIndex >= tabs;

                if (issue.HasWork == false && issue.EmptyAfterClean == false && issue.OffTab == false)
                    continue;

                report.Groups.Add(issue);

                report.DeadIds += issue.Dead.Count;
                report.DuplicateIds += issue.Duplicates;

                if (issue.HasWork) report.GroupsWithWork++;
                if (issue.EmptyAfterClean) report.EmptyGroups++;
                if (issue.OffTab) report.OffTabGroups++;
            }
        }

        private static void ScanComments(Graph graph, CleanupReport report)
        {
            var comments = GraphInternals.CommentsOf(graph);
            if (comments == null) return;

            foreach (var comment in comments)
                if (IsEmpty(comment)) report.EmptyComments.Add(comment);
        }

        private static void ScanUnconnected(Graph graph, CleanupReport report)
        {
            foreach (var node in graph.Nodes)
                if (IsUnconnected(node)) report.Unconnected.Add(node);
        }

        public static bool IsEmpty(Comment comment) =>
            comment != null && string.IsNullOrWhiteSpace(comment.Text);

        /// <summary>
        /// Public because the cleaner asks it again at the moment it deletes rather than trusting
        /// what the panel was told a minute ago — a node wired up since the scan is no longer an
        /// orphan, and deleting it on the strength of a stale list would be the tool losing work
        /// rather than tidying it.
        /// </summary>
        public static bool IsUnconnected(Node node)
        {
            if (node == null || IsInterfaceNode(node)) return false;

            foreach (var port in node.Ports.Values)
                if (port != null && port.ConnectionCount > 0) return false;

            return true;
        }

        /// <summary>
        /// A subgraph's Input and Output nodes are its signature. An unconnected one is a port the
        /// parent graph is still wiring to, so deleting it as an orphan would break the graph
        /// above this one — where the damage would not be visible from here.
        /// </summary>
        private static bool IsInterfaceNode(Node node)
        {
            var name = node.GetType().Name;

            foreach (var interfaceNode in InterfaceNodes)
                if (name == interfaceNode) return true;

            return false;
        }
    }
}
