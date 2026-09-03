using System.Collections.Generic;
using System.Reflection;
using BlueGraph;

namespace Utilities.Editor
{
    /// <summary>
    /// The two lists a graph keeps beside its nodes — its groups and its comments.
    ///
    /// Both are internal to the graph's own assembly while the classes in them are public, so the
    /// only thing missing is the way in. Reached by reflection rather than by SerializedProperty
    /// because the tools that use these need the objects themselves: a Group has to be matched
    /// against the GroupView drawn for it, which a property path cannot do.
    ///
    /// Anything that edits what comes back takes an Undo snapshot of the graph first, the same as
    /// every other whole-asset edit here.
    /// </summary>
    public static class GraphInternals
    {
        private static FieldInfo _groups;
        private static FieldInfo _comments;

        public static List<Group> GroupsOf(Graph graph)
        {
            if (graph == null) return null;

            _groups ??= typeof(Graph).GetField("_groups", BindingFlags.Instance | BindingFlags.NonPublic);

            return _groups?.GetValue(graph) as List<Group>;
        }

        public static List<Comment> CommentsOf(Graph graph)
        {
            if (graph == null) return null;

            _comments ??= typeof(Graph).GetField("_comments", BindingFlags.Instance | BindingFlags.NonPublic);

            return _comments?.GetValue(graph) as List<Comment>;
        }
    }
}
