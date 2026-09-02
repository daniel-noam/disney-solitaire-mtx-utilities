using System.Collections.Generic;
using System.Reflection;
using BlueGraph;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using SuperPlay.Domino.TemplatesBehavior.Runtime.Nodes;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>One thing in a graph that carries a version, and where it was found.</summary>
    public sealed class VersionContributor
    {
        public string Version { get; }

        /// <summary>The node's type name, which is what the graph shows on it.</summary>
        public string Name { get; }

        /// <summary>The subgraphs walked through to reach it, or empty when it is in this graph.</summary>
        public string Path { get; }

        /// <summary>The graph it actually lives in, which is what has to be opened to show it.</summary>
        public Graph Graph { get; }

        public Node Node { get; }

        public VersionContributor(string version, string name, string path, Graph graph, Node node)
        {
            Version = version;
            Name = name;
            Path = path ?? string.Empty;
            Graph = graph;
            Node = node;
        }
    }

    /// <summary>
    /// Which nodes are responsible for a template's minimum client version.
    ///
    /// <see cref="Graph.GetMinVersion"/> answers what the version is by taking the highest it finds
    /// and discarding everything else, so a template that says 1.23.0 gives you no way to learn
    /// which of two hundred nodes said so. This walks the same ground - every node, and every
    /// subgraph recursively - and keeps what it found rather than only how high it was.
    ///
    /// Kept in step with Graph.GetMinVersion deliberately: if that one ever counts something new,
    /// this has to count it too, or the culprit list will not add up to the number beside it.
    /// </summary>
    public static class TemplateVersionAnalyzer
    {
        /// <summary>Guards a subgraph that reaches itself, which would otherwise recurse forever.</summary>
        private const int MaxDepth = 16;

        public static List<VersionContributor> Analyze(Graph graph)
        {
            var found = new List<VersionContributor>();
            if (graph == null) return found;

            Walk(graph, string.Empty, new HashSet<Graph>(), 0, found);
            return found;
        }

        /// <summary>
        /// The version the contributors add up to. Empty list means nothing raised it, which is the
        /// default rather than "no version".
        /// </summary>
        public static string Resolve(IReadOnlyList<VersionContributor> contributors)
        {
            if (contributors == null || contributors.Count == 0) return SolitaireVersions.VERSION_DEFAULT;

            var versions = new List<string>(contributors.Count);
            foreach (var contributor in contributors) versions.Add(contributor.Version);

            return SolitaireVersions.GetMaxVersion(versions);
        }

        /// <summary>
        /// What the template would ask for if everything at <paramref name="version"/> were gone.
        /// The whole point of the list: the number only drops if you deal with all of the top tier.
        /// </summary>
        public static string Without(IReadOnlyList<VersionContributor> contributors, string version)
        {
            var rest = new List<VersionContributor>();
            foreach (var contributor in contributors)
                if (contributor.Version != version) rest.Add(contributor);

            return Resolve(rest);
        }

        private static void Walk(Graph graph, string path, HashSet<Graph> visited, int depth,
            List<VersionContributor> found)
        {
            if (graph == null || depth > MaxDepth || !visited.Add(graph)) return;

            foreach (var node in graph.Nodes)
            {
                if (node == null) continue;

                var version = VersionOf(node.GetType());
                if (version != null)
                    found.Add(new VersionContributor(version, node.GetType().Name, path, graph, node));

                // A subgraph raises its parent to whatever it needs, so the thing to point at is
                // inside it — naming the subgraph node alone would send you to a node that is only
                // guilty by association.
                if (node is ISubGraphNode subgraph && subgraph.SubGraph != null && subgraph.SubGraph != graph)
                    Walk(subgraph.SubGraph, Join(path, node.GetType().Name), visited, depth + 1, found);
            }

            // Graph.GetMinVersion also counts the graph's groups, and this does not: Groups is
            // internal to that assembly, so it cannot be read from here. Nothing is lost — Group
            // carries [SolitaireVersion(VERSION_0_38_0)], which is the default, so a group can
            // never be the answer to what raised a template above the baseline.
        }

        private static string VersionOf(System.Type type) =>
            type.GetCustomAttribute<SolitaireVersionAttribute>()?.Version;

        private static string Join(string path, string step) =>
            string.IsNullOrEmpty(path) ? step : path + " › " + step;
    }
}
