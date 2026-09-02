using System.Collections.Generic;
using System.Linq;
using BlueGraph;
using BlueGraph.Editor;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using SuperPlay.Domino.TemplatesBehavior.Runtime.Nodes;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Adds the graph's size to its inspector, under the min version: how many nodes it holds, how
    /// many once its subgraphs are counted, and how many of them nothing calls.
    ///
    /// The same three numbers the graph editor's Performance Stats panel shows, which is the point:
    /// they are worth knowing before opening a template, not only once you are inside it.
    ///
    /// Extends BlueGraph's own GraphEditor rather than replacing it, so the Open button, the script
    /// field and the min version keep coming from there. It wins over the base for this type because
    /// TemplateBehavior is the more specific target.
    ///
    /// Deliberately declares no OnEnable: the base keeps its target in a private field it sets in
    /// its own, and Unity would call this one instead, leaving that field null under everything the
    /// base draws.
    /// </summary>
    [CustomEditor(typeof(TemplateBehavior))]
    public class TemplateBehaviorInspector : GraphEditor
    {
        /// <summary>How often the counts are rebuilt while the inspector is open, in seconds.</summary>
        private const double Interval = 0.5;

        private double _next;

        // Qualified: the runtime namespace has an Object of its own, and this is Unity's.
        private UnityEngine.Object _counted;
        private int _nodes;
        private int _nodesWithSubgraphs;
        private int _uncalled;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            var graph = target as Graph;
            if (graph == null) return;

            Recount(graph);

            // Read-outs, not fields: matching the min version above, which is also a disabled text
            // field rather than a label, so the numbers line up with it and can be copied.
            var enabled = GUI.enabled;
            GUI.enabled = false;

            EditorGUILayout.TextField("Node Count", _nodes.ToString());
            EditorGUILayout.TextField("Node Count With Subgraphs", _nodesWithSubgraphs.ToString());
            EditorGUILayout.TextField("Uncalled Node Count", _uncalled.ToString());

            GUI.enabled = enabled;
        }

        /// <summary>
        /// Throttled: an inspector repaints on mouse movement, and the recursive count walks every
        /// node of every subgraph. Half a second is well inside the time it takes to edit a graph
        /// and notice the number is wrong.
        /// </summary>
        private void Recount(Graph graph)
        {
            if (target == _counted && EditorApplication.timeSinceStartup < _next) return;

            _next = EditorApplication.timeSinceStartup + Interval;
            _counted = target;

            _nodes = graph.Nodes.Count;
            _nodesWithSubgraphs = CountWithSubgraphs(graph, new HashSet<Graph>());
            _uncalled = graph.Nodes.Count(node => node != null && IsUncalled(node));
        }

        /// <summary>
        /// Every node the graph reaches, subgraphs included. The visited set is what stops a
        /// subgraph that reaches back up from counting forever, and also keeps a graph reached
        /// twice from being counted twice.
        /// </summary>
        private static int CountWithSubgraphs(Graph graph, HashSet<Graph> visited)
        {
            if (graph == null || !visited.Add(graph)) return 0;

            var count = 0;
            foreach (var node in graph.Nodes)
            {
                if (node == null) continue;

                count++;
                if (node is ISubGraphNode subgraph) count += CountWithSubgraphs(subgraph.SubGraph, visited);
            }

            return count;
        }

        /// <summary>
        /// Nothing runs this node: it has a Prev port that nothing feeds, or it has no Prev port at
        /// all and none of its ports are connected — a value node left floating.
        ///
        /// The same rule the graph editor counts by. If that one ever changes, this has to change
        /// with it or the two will disagree about the same graph.
        /// </summary>
        private static bool IsUncalled(Node node)
        {
            var prev = node.GetPort(PortNames.EXECUTION_NODE_PREV);
            if (prev != null) return prev.ConnectionCount == 0;

            foreach (var port in node.Ports.Values)
                if (port.ConnectionCount > 0) return false;

            return true;
        }
    }
}
