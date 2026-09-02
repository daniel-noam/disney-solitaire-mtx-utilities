using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlueGraph;
using BlueGraph.Editor;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>One deprecated node in a graph, and what replacing it would cost.</summary>
    public sealed class NodeUpgrade
    {
        public Node Node { get; }

        /// <summary>The node's own label, as the graph shows it.</summary>
        public string Name { get; }

        /// <summary>What the author said to use instead, or null when they only said "deprecated".</summary>
        public Type Replacement { get; }

        /// <summary>The deprecation message, for the ones with nowhere to go.</summary>
        public string Message { get; }

        /// <summary>
        /// True when the node itself named its replacement. False means it was worked out from the
        /// naming, which is a guess — a good one in this codebase, but still worth saying out loud.
        /// </summary>
        public bool Declared { get; }

        /// <summary>Connected ports the replacement has no counterpart for. Empty is the good case.</summary>
        public IReadOnlyList<string> DroppedPorts { get; }

        /// <summary>Values that would not survive the swap, by field name.</summary>
        public IReadOnlyList<string> DroppedFields { get; }

        public bool CanUpgrade => Replacement != null;
        public bool IsClean => CanUpgrade && DroppedPorts.Count == 0 && DroppedFields.Count == 0;

        public NodeUpgrade(Node node, string name, Type replacement, bool declared, string message,
            IReadOnlyList<string> droppedPorts, IReadOnlyList<string> droppedFields)
        {
            Node = node;
            Name = name;
            Replacement = replacement;
            Declared = declared;
            Message = message;
            DroppedPorts = droppedPorts;
            DroppedFields = droppedFields;
        }
    }

    /// <summary>An edge the swap could not rebuild, and what it used to join.</summary>
    public sealed class DroppedConnection
    {
        /// <summary>The port on the old node that had no counterpart.</summary>
        public string Port { get; }

        /// <summary>The node at the other end, which is still sitting there unconnected.</summary>
        public string OtherNode { get; }

        public string OtherPort { get; }

        public DroppedConnection(string port, string otherNode, string otherPort)
        {
            Port = port;
            OtherNode = otherNode;
            OtherPort = otherPort;
        }

        public override string ToString() => $"{Port} ↔ {OtherNode}.{OtherPort}";
    }

    /// <summary>What a swap actually did, as opposed to what it was expected to do.</summary>
    public sealed class UpgradeResult
    {
        public Node Replacement { get; }

        /// <summary>Empty when nothing was lost, which is the ordinary case.</summary>
        public IReadOnlyList<DroppedConnection> Dropped { get; }

        public UpgradeResult(Node replacement, IReadOnlyList<DroppedConnection> dropped)
        {
            Replacement = replacement;
            Dropped = dropped;
        }
    }

    /// <summary>
    /// Swaps a deprecated node for the one its author named in [Deprecated(ReplaceWith = ...)],
    /// keeping the connections and the field values that both versions share.
    ///
    /// The replacement is not guessed: the attribute says which type supersedes which, so this
    /// only ever does what the node's own author already wrote down. What it adds is carrying the
    /// wiring across, which is the part that makes doing it by hand tedious enough to put off.
    ///
    /// Ports and fields are matched by name. A V2 node is normally the old one plus something, so
    /// most of the time everything carries; when it does not, the mismatch is reported before the
    /// swap rather than discovered after it.
    /// </summary>
    public static class DeprecatedNodeUpgrader
    {
        /// <summary>How far past a node's own version to look for a newer one.</summary>
        private const int MaxVersionsAhead = 8;

        /// <summary>Every deprecated node in this graph, whether or not it has somewhere to go.</summary>
        public static List<NodeUpgrade> Scan(Graph graph)
        {
            var found = new List<NodeUpgrade>();
            if (graph == null) return found;

            foreach (var node in graph.Nodes)
            {
                if (node == null) continue;

                var deprecated = node.GetType().GetCustomAttribute<DeprecatedAttribute>();
                if (deprecated == null) continue;

                var replacement = ResolveCached(node.GetType(), out var declared);
                var droppedPorts = new List<string>();
                var droppedFields = new List<string>();

                if (replacement != null) Diff(node, replacement, droppedPorts, droppedFields);

                found.Add(new NodeUpgrade(node, node.Name, replacement, declared, deprecated.Message,
                    droppedPorts, droppedFields));
            }

            return found;
        }

        /// <summary>
        /// Replaces the node in the graph and returns what it became, or null when it could not be
        /// done at all, having changed nothing.
        ///
        /// Records no undo of its own. Undo.RecordObject snapshots the whole graph asset, which on
        /// a large one is the most expensive thing here — so the caller takes it once for whatever
        /// the person actually did, rather than once per node in a batch of forty.
        /// </summary>
        public static UpgradeResult Apply(Graph graph, NodeUpgrade upgrade)
        {
            if (graph == null || upgrade == null || !upgrade.CanUpgrade) return null;

            var old = upgrade.Node;
            if (old == null || !graph.Nodes.Contains(old)) return null;

            Node replacement;
            try
            {
                replacement = NodeReflection.Instantiate(upgrade.Replacement);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }

            if (replacement == null) return null;

            // Where it sat and which tab it was on, so the graph looks the same afterwards. The
            // name is deliberately not copied: the new type carries its own, and the old one often
            // reads "(Deprecated)".
            replacement.Position = old.Position;
            replacement.TabIndex = old.TabIndex;

            CopyFields(old, replacement);

            graph.AddNode(replacement);

            // What was cut is collected as it happens, from the edges that were really there —
            // the prediction made before the swap is a different thing, and only this one can be
            // handed to somebody as a list of what to go and fix.
            var dropped = new List<DroppedConnection>();
            Reconnect(graph, old, replacement, dropped);

            InheritGroups(graph, old.ID, replacement.ID);

            // Removed last: the edges are read off it up to this point.
            graph.RemoveNode(old);

            EditorUtility.SetDirty(graph);
            return new UpgradeResult(replacement, dropped);
        }

        /// <summary>
        /// Takes the old node's place in any group it belonged to.
        ///
        /// A group holds the ids of its members, so a replacement is outside every group it was in
        /// until its id is put where the old one's was — the node comes back sitting on top of the
        /// group it used to be part of, which looks like the group lost it.
        ///
        /// Reached by reflection because Graph.Groups is internal to its own assembly. Group itself
        /// is public, so only the way in is awkward.
        /// </summary>
        private static void InheritGroups(Graph graph, string oldId, string newId)
        {
            var groups = GroupsOf(graph);
            if (groups == null) return;

            foreach (var group in groups)
            {
                if (group?.nodes == null) continue;

                for (var i = 0; i < group.nodes.Count; i++)
                    if (group.nodes[i] == oldId) group.nodes[i] = newId;
            }
        }

        private static List<Group> GroupsOf(Graph graph)
        {
            if (GroupsField == null)
                GroupsField = typeof(Graph).GetField("_groups",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return GroupsField?.GetValue(graph) as List<Group>;
        }

        private static FieldInfo GroupsField;

        /// <summary>
        /// Rebuilds every edge the old node had, on the ports the new one shares by name.
        ///
        /// Read into a list first: adding edges while walking the old node's own connections would
        /// be modifying the thing being enumerated.
        /// </summary>
        private static void Reconnect(Graph graph, Node old, Node replacement, List<DroppedConnection> dropped)
        {
            var edges = new List<(Port from, Port to)>();

            var from = old.GetType();
            var to = replacement.GetType();

            foreach (var port in old.Ports.Values)
            {
                var mirrored = replacement.GetPort(NodeUpgradeAliases.Port(from, to, port.Name));
                if (mirrored == null || mirrored.Direction != port.Direction)
                {
                    foreach (var orphaned in port.ConnectedPorts)
                        if (orphaned != null)
                            dropped.Add(new DroppedConnection(port.Name,
                                orphaned.Node != null ? orphaned.Node.Name : "?", orphaned.Name));

                    continue;
                }

                foreach (var other in port.ConnectedPorts)
                {
                    if (other == null) continue;

                    // AddEdge takes them output-first, whichever end of this pair the old node was.
                    edges.Add(port.Direction == PortDirection.Output
                        ? (mirrored, other)
                        : (other, mirrored));
                }
            }

            foreach (var edge in edges)
                graph.AddEdge(edge.from, edge.to);
        }

        /// <summary>
        /// Copies the values both types declare under the same name. Only the fields the node
        /// itself adds — everything on Node is identity and layout, which is handled separately or
        /// deliberately left behind.
        /// </summary>
        private static void CopyFields(Node old, Node replacement)
        {
            var from = old.GetType();
            var to = replacement.GetType();

            foreach (var field in SerializedFields(from))
            {
                var target = Counterpart(from, to, field);
                if (target == null) continue;

                if (TryCarry(field.GetValue(old), target.FieldType, out var value))
                    target.SetValue(replacement, value);
            }
        }

        private static FieldInfo Counterpart(Type from, Type to, FieldInfo field) =>
            to.GetField(NodeUpgradeAliases.Field(from, to, field.Name),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Whether a value survives the move to the new field's type, and what it becomes.
        ///
        /// Plain assignment covers most of it. Two more cases are worth taking, because they are
        /// exactly what a V2 tends to change and both are lossless:
        ///
        ///   - a wider number. IntCompareV2 holds its operands as long where IntCompare held int.
        ///     Reflection does not consider int assignable to long, so without this the values a
        ///     comparison is built on would silently be left behind.
        ///
        ///   - the same enum, redeclared. A node nests its own Mode, so IntCompare.Mode and
        ///     IntCompareV2.Mode are unrelated types holding the same names. Matched by name, so
        ///     Greater stays Greater rather than becoming whatever is at that number.
        /// </summary>
        private static bool TryCarry(object value, Type to, out object carried)
        {
            carried = null;

            if (value == null) return !to.IsValueType || Nullable.GetUnderlyingType(to) != null;
            if (to.IsInstanceOfType(value))
            {
                carried = value;
                return true;
            }

            var from = value.GetType();

            if (from.IsEnum && to.IsEnum)
            {
                var name = Enum.GetName(from, value);

                // A member the new enum does not have. Carrying the number instead would quietly
                // change what the node does, so this is a loss and is reported as one.
                if (name == null || !Enum.IsDefined(to, name)) return false;

                carried = Enum.Parse(to, name);
                return true;
            }

            if (!Widens(from, to)) return false;

            carried = Convert.ChangeType(value, to);
            return true;
        }

        /// <summary>
        /// Numeric moves that cannot lose anything, spelled out rather than left to Convert, which
        /// would just as happily truncate a long into an int.
        /// </summary>
        private static bool Widens(Type from, Type to)
        {
            var source = Type.GetTypeCode(from);
            var target = Type.GetTypeCode(to);

            switch (source)
            {
                case TypeCode.Byte:
                    return target == TypeCode.Int16 || target == TypeCode.Int32 || target == TypeCode.Int64 ||
                           target == TypeCode.Single || target == TypeCode.Double;
                case TypeCode.Int16:
                    return target == TypeCode.Int32 || target == TypeCode.Int64 ||
                           target == TypeCode.Single || target == TypeCode.Double;
                case TypeCode.Int32:
                    return target == TypeCode.Int64 || target == TypeCode.Double;
                case TypeCode.Single:
                    return target == TypeCode.Double;
                default:
                    return false;
            }
        }

        /// <summary>What would not survive, so the caller can say so before anything happens.</summary>
        private static void Diff(Node node, Type replacement, List<string> droppedPorts, List<string> droppedFields)
        {
            var probe = Probe(replacement);
            if (probe == null)
            {
                // Cannot be built, so nothing can be said about it — treated as no replacement at
                // all by the caller, which checks the ports it got back.
                droppedPorts.Add("could not create " + replacement.Name);
                return;
            }

            var from = node.GetType();

            foreach (var port in node.Ports.Values)
            {
                if (port.ConnectionCount == 0) continue;

                var mirrored = probe.GetPort(NodeUpgradeAliases.Port(from, replacement, port.Name));
                if (mirrored == null || mirrored.Direction != port.Direction) droppedPorts.Add(port.Name);
            }

            // Checked against the value this node actually holds, not just the field's type: an
            // enum member the new node dropped is only a loss for the nodes set to it.
            foreach (var field in SerializedFields(from))
            {
                var target = Counterpart(from, replacement, field);

                if (target == null || !TryCarry(field.GetValue(node), target.FieldType, out _))
                    droppedFields.Add(field.Name);
            }
        }

        /// <summary>
        /// The node's own serialized fields, not the base's. Walks up to Node and stops, so ID,
        /// Position, Ports and the rest of the machinery are never treated as data to copy.
        /// </summary>
        private static IEnumerable<FieldInfo> SerializedFields(Type type)
        {
            for (var current = type; current != null && current != typeof(Node); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized) continue;
                    if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;

                    yield return field;
                }
            }
        }

        /// <summary>
        /// <see cref="Resolve"/>, remembered per type. A graph holds many nodes of the same type,
        /// and the answer depends only on the type, so it is worked out once per assembly load.
        /// </summary>
        private static Type ResolveCached(Type old, out bool declared)
        {
            if (Resolved.TryGetValue(old, out var cached))
            {
                declared = cached.Declared;
                return cached.Replacement;
            }

            var replacement = Resolve(old, out declared);
            Resolved[old] = (replacement, declared);
            return replacement;
        }

        private static readonly Dictionary<Type, (Type Replacement, bool Declared)> Resolved =
            new Dictionary<Type, (Type, bool)>();

        /// <summary>
        /// One built instance of a replacement type, kept to compare against. Building a node
        /// reflects over its ports, and forty deprecated nodes of the same type asked the same
        /// question forty times.
        ///
        /// Only ever read from — the copy that goes into the graph is built fresh in Apply.
        /// </summary>
        private static Node Probe(Type type)
        {
            if (Probes.TryGetValue(type, out var probe)) return probe;

            try
            {
                probe = NodeReflection.Instantiate(type);
            }
            catch (Exception)
            {
                probe = null;
            }

            Probes[type] = probe;
            return probe;
        }

        private static readonly Dictionary<Type, Node> Probes = new Dictionary<Type, Node>();

        /// <summary>
        /// What this node should become. The attribute first; failing that, the next version along
        /// by name.
        ///
        /// Most deprecated nodes here have a V2 sitting beside them and no attribute pointing at
        /// it — only a handful were ever filled in — so going by the attribute alone would leave
        /// the majority of them looking like they had nowhere to go.
        /// </summary>
        private static Type Resolve(Type old, out bool declared)
        {
            declared = true;

            var stated = FollowDeclared(old);
            if (stated != null) return stated;

            declared = false;
            return Infer(old);
        }

        /// <summary>
        /// Follows ReplaceWith as far as it goes: a node can name a successor that is itself
        /// deprecated, and landing on that would be an upgrade to something else's problem.
        /// </summary>
        private static Type FollowDeclared(Type type)
        {
            var seen = new HashSet<Type> { type };
            Type last = null;

            for (var current = type; current != null;)
            {
                var deprecated = current.GetCustomAttribute<DeprecatedAttribute>();
                var next = Close(deprecated?.ReplaceWith, current);

                if (next == null || !seen.Add(next)) break;

                last = next;
                current = next;
            }

            return last;
        }

        /// <summary>
        /// The newest sibling that is not itself deprecated: Foo, FooV2, FooV3. Abstract types are
        /// skipped — PresentFlyTextRewardsAbstractV2 is a base class, not something to drop into a
        /// graph — and a newest that is itself deprecated hands over to whatever it names.
        ///
        /// Searched across the whole assembly by name rather than beside the old node: the newer
        /// versions were largely moved into a Nodes namespace of their own, so IntCompare and
        /// IntCompareV2 do not share one, and looking only where the old node lives finds nothing
        /// for most of the pairs there are.
        /// </summary>
        private static Type Infer(Type old)
        {
            var stem = Stem(old.Name, out var version);
            if (stem == null) return null;

            var byName = NodesByName(old.Assembly);
            Type best = null;

            for (var candidate = version + 1; candidate <= version + MaxVersionsAhead; candidate++)
            {
                if (!byName.TryGetValue(stem + "V" + candidate, out var matches)) continue;

                var picked = Pick(matches, old.Namespace);
                if (picked != null) best = picked;
            }

            if (best == null) return null;

            return best.GetCustomAttribute<DeprecatedAttribute>() != null
                ? FollowDeclared(best) ?? best
                : best;
        }

        /// <summary>
        /// One type from the candidates sharing a name. Its own namespace first, then the only one
        /// there is — with two unrelated types of the same name, guessing would be picking one of
        /// somebody's nodes at random, so it declines instead.
        /// </summary>
        private static Type Pick(List<Type> matches, string preferredNamespace)
        {
            if (matches.Count == 1) return matches[0];

            foreach (var match in matches)
                if (match.Namespace == preferredNamespace) return match;

            return null;
        }

        /// <summary>
        /// Every concrete node in the assembly, by simple name. Built once: this walks a few
        /// thousand types, and the answer cannot change without a domain reload, which clears it.
        /// </summary>
        private static Dictionary<string, List<Type>> NodesByName(Assembly assembly)
        {
            if (NodeIndex.TryGetValue(assembly, out var index)) return index;

            index = new Dictionary<string, List<Type>>();

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(Node).IsAssignableFrom(type)) continue;

                if (!index.TryGetValue(type.Name, out var list))
                {
                    list = new List<Type>();
                    index[type.Name] = list;
                }

                list.Add(type);
            }

            NodeIndex[assembly] = index;
            return index;
        }

        private static readonly Dictionary<Assembly, Dictionary<string, List<Type>>> NodeIndex =
            new Dictionary<Assembly, Dictionary<string, List<Type>>>();

        /// <summary>
        /// The name without its version suffix, and the version it carried. "PlayAudio" is version
        /// 1; "PlayAudioV2" is 2 with the same stem, so both search the same family.
        /// </summary>
        private static string Stem(string name, out int version)
        {
            version = 1;
            if (string.IsNullOrEmpty(name)) return null;

            var end = name.Length;
            var digits = end;
            while (digits > 0 && name[digits - 1] >= '0' && name[digits - 1] <= '9') digits--;

            if (digits == end || digits == 0 || name[digits - 1] != 'V') return name;

            if (!int.TryParse(name.Substring(digits, end - digits), out version)) version = 1;
            return name.Substring(0, digits - 1);
        }

        /// <summary>
        /// The replacement as a type that can actually be built. ForEach&lt;T&gt; names ForEachV2&lt;&gt;
        /// as its replacement — an open generic, which is closed here with the arguments the old
        /// node was using.
        /// </summary>
        private static Type Close(Type replacement, Type old)
        {
            if (replacement == null || !replacement.IsGenericTypeDefinition) return replacement;

            if (!old.IsGenericType) return null;

            var arguments = old.GetGenericArguments();
            if (arguments.Length != replacement.GetGenericArguments().Length) return null;

            try
            {
                return replacement.MakeGenericType(arguments);
            }
            catch (Exception)
            {
                // Constraints the old node's arguments do not satisfy: nothing to offer here.
                return null;
            }
        }
    }
}
