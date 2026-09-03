using System.Collections.Generic;
using System.Reflection;
using BlueGraph;
using UnityEditor;

namespace Utilities.Editor
{
    /// <summary>
    /// Renames a method id or trigger name across every node in one graph that uses it.
    ///
    /// All of them, in one call, or none: these strings are a join, so a rename that reaches some
    /// uses and not others does not leave a graph half-renamed, it leaves a graph that no longer
    /// runs — and it does it without an error, which is the whole reason this exists.
    /// </summary>
    public static class GraphIdentifierRefactorer
    {
        /// <summary>
        /// How many nodes were written to. Not the same as how many nodes use the name: several
        /// can read it from one String node, and that node is written once.
        /// </summary>
        public static int Rename(Graph graph, IdentifierGroup group, string newValue)
        {
            if (graph == null || group == null || group.CanRename == false) return 0;
            if (string.IsNullOrEmpty(newValue) || newValue == group.Value) return 0;

            // Keyed by node id, which both finds the node in the serialized array and collapses
            // the several uses that share one String node into the one write they really are.
            var targets = new Dictionary<string, FieldInfo>();

            foreach (var use in group.Uses)
            {
                if (use.CanWrite == false || use.ValueNode == null) continue;
                targets[use.ValueNode.ID] = use.Field;
            }

            if (targets.Count == 0) return 0;

            var serialized = new SerializedObject(graph);
            var nodes = serialized.FindProperty("nodes");
            if (nodes == null || nodes.isArray == false) return 0;

            var renamed = 0;

            // By id rather than by position: Graph.Nodes hands back a filtered copy that drops
            // nulls, so its indices are not the serialized array's whenever a node has failed to
            // deserialize — which is exactly the graph you would least like this to be wrong on.
            for (var i = 0; i < nodes.arraySize; i++)
            {
                var element = nodes.GetArrayElementAtIndex(i);
                if (element == null) continue;

                var id = element.FindPropertyRelative("id");
                if (id == null || targets.TryGetValue(id.stringValue, out var field) == false) continue;

                var value = element.FindPropertyRelative(field.Name);
                if (value == null || value.propertyType != SerializedPropertyType.String) continue;

                value.stringValue = newValue;
                renamed++;
            }

            if (renamed == 0) return 0;

            // Through SerializedObject rather than by reflection, so this lands as one undo step
            // and the canvas's own bound fields see the change, the same as a hand edit would.
            Undo.SetCurrentGroupName((group.Kind == IdentifierKind.Method ? "Method" : "Trigger") + " rename");
            serialized.ApplyModifiedProperties();

            AssetDatabase.SaveAssetIfDirty(graph);

            return renamed;
        }
    }
}
