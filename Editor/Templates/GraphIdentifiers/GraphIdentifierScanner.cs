using System;
using System.Collections.Generic;
using System.Reflection;
using BlueGraph;

namespace Utilities.Editor
{
    /// <summary>The two string namespaces a graph wires itself together with.</summary>
    public enum IdentifierKind
    {
        Method,
        Trigger,
    }

    /// <summary>One node that names an identifier, and where that name is actually written down.</summary>
    public sealed class IdentifierUse
    {
        /// <summary>The node that uses the name — what "Find" goes to.</summary>
        public Node Node { get; }

        /// <summary>What this node does with the name: defines, calls, sets, listens.</summary>
        public string Role { get; }

        /// <summary>
        /// The node holding the string. The same as <see cref="Node"/> when it is typed on the
        /// node itself, and the String node upstream when the port is fed by an edge instead.
        /// </summary>
        public Node ValueNode { get; }

        /// <summary>
        /// The field on <see cref="ValueNode"/> that holds it, or null when there is nothing to
        /// write to — a key returned by code, or a value this cannot account for.
        /// </summary>
        public FieldInfo Field { get; }

        /// <summary>True when the name arrives down an edge rather than being typed on the node.</summary>
        public bool ViaConstant => ValueNode != Node;

        /// <summary>
        /// True when the node holding the string also feeds something that is not part of this
        /// group. Renaming would then change that other thing too, so it does not happen.
        /// </summary>
        public bool Shared { get; set; }

        public bool CanWrite => Field != null && Shared == false;

        public IdentifierUse(Node node, string role, Node valueNode, FieldInfo field)
        {
            Node = node;
            Role = role;
            ValueNode = valueNode;
            Field = field;
        }
    }

    /// <summary>Every node in one graph that names the same identifier.</summary>
    public sealed class IdentifierGroup
    {
        public IdentifierKind Kind { get; }

        public string Value { get; }

        public IReadOnlyList<IdentifierUse> Uses { get; }

        /// <summary>
        /// False when any one use cannot be written to. Renaming the rest would split the group
        /// and quietly unwire it, which is worse than not renaming at all — so it is all of them
        /// or none.
        /// </summary>
        public bool CanRename
        {
            get
            {
                if (string.IsNullOrEmpty(Value)) return false;

                foreach (var use in Uses)
                    if (use.CanWrite == false) return false;

                return true;
            }
        }

        public IdentifierGroup(IdentifierKind kind, string value, IReadOnlyList<IdentifierUse> uses)
        {
            Kind = kind;
            Value = value;
            Uses = uses;
        }

        /// <summary>How many nodes use it in each role, as "2 calls · 1 defines".</summary>
        public string Summary()
        {
            var counts = new Dictionary<string, int>();
            var order = new List<string>();

            foreach (var use in Uses)
            {
                if (counts.ContainsKey(use.Role) == false) order.Add(use.Role);
                counts[use.Role] = counts.TryGetValue(use.Role, out var n) ? n + 1 : 1;
            }

            var parts = new List<string>(order.Count);
            foreach (var role in order) parts.Add(counts[role] + " " + role);

            return string.Join("  ·  ", parts);
        }

        public bool HasRole(string role)
        {
            foreach (var use in Uses)
                if (use.Role == role) return true;

            return false;
        }

        /// <summary>How many of these take the name from a String node rather than their own field.</summary>
        public int ViaConstants()
        {
            var count = 0;
            foreach (var use in Uses)
                if (use.ViaConstant) count++;

            return count;
        }

        public IdentifierUse FirstBlocked()
        {
            foreach (var use in Uses)
                if (use.CanWrite == false) return use;

            return null;
        }
    }

    /// <summary>What one pass over a graph found.</summary>
    public sealed class IdentifierScan
    {
        public List<IdentifierGroup> Methods { get; } = new List<IdentifierGroup>();
        public List<IdentifierGroup> Triggers { get; } = new List<IdentifierGroup>();

        /// <summary>
        /// Nodes whose name arrives down an edge that no constant explains — it is whatever the
        /// upstream node works out at runtime. Counted and reported rather than grouped: a rename
        /// cannot follow them, and neither can the warnings.
        /// </summary>
        public List<IdentifierUse> EdgeDriven { get; } = new List<IdentifierUse>();

        /// <summary>Subgraph nodes in this graph, which have namespaces of their own.</summary>
        public int Subgraphs { get; set; }
    }

    /// <summary>
    /// The method ids and trigger names in a graph, grouped by the name itself rather than by node.
    ///
    /// These strings are the only thing joining a CallMethod to its OnMethod, or a SetTrigger to
    /// the OnTrigger waiting for it. Nothing checks them: a typo on one end is not an error, it is
    /// a flow that silently never runs, which is why renaming one by hand is a job people avoid.
    ///
    /// Scope is the one graph asset, deliberately. Origin nodes only ever fire from the top-level
    /// behaviour's own node list, and CallMethod resolves against the graph it sits in, so a name
    /// inside a subgraph is a separate namespace that this must not reach into.
    /// </summary>
    public static class GraphIdentifierScanner
    {
        /// <summary>
        /// Which node carries which identifier, by type name and port name.
        ///
        /// Matched by name rather than by type on purpose: OnMethod and CallMethod live in the
        /// Solitaire behaviour assembly, which this one does not reference and a Domino project
        /// need not have. Referencing it to gain compile-time safety here would cost the toolset
        /// its portability, which is the worse trade — and there is precedent in
        /// BindingGraphRefactorer, which reaches PlayAudio the same way.
        ///
        /// SetAnimatorTrigger is deliberately absent. Its "Trigger Name" is an Animator parameter,
        /// a different namespace entirely, and renaming one from here would be renaming the wrong
        /// thing under a name that looks right.
        /// </summary>
        private static readonly (string Type, string Port, IdentifierKind Kind, string Role)[] Carriers =
        {
            ("OnMethod", "Id", IdentifierKind.Method, "defines"),
            ("CallMethod", "Id", IdentifierKind.Method, "calls"),
            ("SetTrigger", "Key", IdentifierKind.Trigger, "sets"),
            ("OnTrigger", "Trigger Name Filter", IdentifierKind.Trigger, "listens"),
            ("OnComponentTrigger", "Trigger Name Filter", IdentifierKind.Trigger, "from a component"),
        };

        /// <summary>
        /// Nodes that set a trigger whose key is a constant in their own code — the mini-collection
        /// ones and out-of-DR. They cannot be renamed, but they occupy the same namespace, so they
        /// are listed: renaming a trigger onto one of these names joins two flows that were never
        /// meant to meet, and nothing else would tell you.
        /// </summary>
        private const string FixedKeyBaseType = "SetTriggerBase";

        private const string FixedKeyMethod = "GetKey";

        private const string SubgraphType = "SubgraphNode";

        public static IdentifierScan Scan(Graph graph)
        {
            var scan = new IdentifierScan();
            if (graph == null) return scan;

            var methods = new Dictionary<string, List<IdentifierUse>>();
            var triggers = new Dictionary<string, List<IdentifierUse>>();

            // Which carrier port each use reads from, so the exclusivity pass below can tell a
            // String node that only feeds this group from one that also feeds something else.
            var ports = new Dictionary<IdentifierUse, string>();

            foreach (var node in graph.Nodes)
            {
                if (node == null) continue;

                var type = node.GetType();

                if (DerivesFrom(type, SubgraphType) || type.Name == SubgraphType)
                {
                    scan.Subgraphs++;
                    continue;
                }

                var matched = false;

                foreach (var carrier in Carriers)
                {
                    if (type.Name != carrier.Type) continue;

                    matched = true;
                    Collect(scan, node, carrier, Bucket(carrier.Kind, methods, triggers), ports);
                    break;
                }

                if (matched) continue;

                if (TryFixedKey(node, out var fixedKey))
                    Add(triggers, fixedKey, new IdentifierUse(node, "sets (fixed)", node, null));
            }

            Fill(scan.Methods, IdentifierKind.Method, methods, ports);
            Fill(scan.Triggers, IdentifierKind.Trigger, triggers, ports);

            return scan;
        }

        private static Dictionary<string, List<IdentifierUse>> Bucket(IdentifierKind kind,
            Dictionary<string, List<IdentifierUse>> methods, Dictionary<string, List<IdentifierUse>> triggers) =>
            kind == IdentifierKind.Method ? methods : triggers;

        private static void Collect(IdentifierScan scan, Node node,
            (string Type, string Port, IdentifierKind Kind, string Role) carrier,
            Dictionary<string, List<IdentifierUse>> bucket, Dictionary<IdentifierUse, string> ports)
        {
            var port = node.GetPort(carrier.Port);

            // An editable input port only reads its own field while nothing is plugged into it.
            // With an edge attached the field is dead weight and the name lives upstream — which
            // is not the exception here: wiring a String node into Id is how most of these graphs
            // are built, so a tool that gave up at the first edge would be read-only in practice.
            if (port != null && port.ConnectionCount > 0)
            {
                if (TryUpstreamConstant(port, out var upstream, out var provider, out var field) &&
                    string.IsNullOrEmpty(upstream) == false)
                {
                    var use = new IdentifierUse(node, carrier.Role, provider, field);
                    ports[use] = carrier.Port;
                    Add(bucket, upstream, use);
                    return;
                }

                scan.EdgeDriven.Add(new IdentifierUse(node, carrier.Role, node, null));
                return;
            }

            var own = FindValueField(node.GetType(), carrier.Port);
            if (own == null) return;

            var value = own.GetValue(node) as string ?? string.Empty;

            var typed = new IdentifierUse(node, carrier.Role, node, own);
            ports[typed] = carrier.Port;
            Add(bucket, value, typed);
        }

        /// <summary>
        /// The name an edge carries, and the node it is written on, when it comes from a node
        /// holding a constant. Anything computed at runtime has no answer here, which is the
        /// honest result rather than a guess.
        /// </summary>
        private static bool TryUpstreamConstant(Port port, out string value, out Node provider, out FieldInfo field)
        {
            value = null;
            provider = null;
            field = null;

            // Resolving walks into the upstream node's own code, which is being asked outside the
            // runtime it expects — not worth taking the panel down for.
            try
            {
                if (port.TryGetConstantValue(out value) == false) return false;
            }
            catch (Exception)
            {
                return false;
            }

            foreach (var connected in port.ConnectedPorts)
            {
                // Null when the graph has not resolved its edges. Nothing can be said about a
                // connection that is not there yet, so it counts as unknown rather than as absent.
                if (connected == null || connected.Node == null) return false;

                provider = connected.Node;
                field = FindValueField(provider.GetType(), connected.Name);
                break;
            }

            return provider != null;
        }

        private static void Add(Dictionary<string, List<IdentifierUse>> bucket, string value, IdentifierUse use)
        {
            if (bucket.TryGetValue(value, out var uses) == false)
            {
                uses = new List<IdentifierUse>();
                bucket[value] = uses;
            }

            uses.Add(use);
        }

        /// <summary>Alphabetical, with the unnamed ones first — they are the ones to deal with.</summary>
        private static void Fill(List<IdentifierGroup> into, IdentifierKind kind,
            Dictionary<string, List<IdentifierUse>> bucket, Dictionary<IdentifierUse, string> ports)
        {
            var values = new List<string>(bucket.Keys);
            values.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var value in values)
            {
                var uses = bucket[value];
                MarkShared(uses, ports);

                var group = new IdentifierGroup(kind, value, uses);

                if (string.IsNullOrEmpty(value)) into.Insert(0, group);
                else into.Add(group);
            }
        }

        /// <summary>
        /// A String node wired into an Id is usually there for that one node, and renaming it is
        /// the rename. But nothing stops one feeding a label and an Id at once, and renaming that
        /// would change the label too — so a provider is only writable while everything it feeds
        /// is inside this group.
        /// </summary>
        private static void MarkShared(List<IdentifierUse> uses, Dictionary<IdentifierUse, string> ports)
        {
            var inGroup = new HashSet<string>();
            foreach (var use in uses)
                if (use.Node != null && ports.TryGetValue(use, out var portName))
                    inGroup.Add(use.Node.ID + "\n" + portName);

            foreach (var use in uses)
            {
                if (use.ViaConstant == false || use.ValueNode == null) continue;

                foreach (var port in use.ValueNode.Ports.Values)
                {
                    if (port == null || port.Direction != PortDirection.Output) continue;

                    foreach (var consumer in port.ConnectedPorts)
                    {
                        if (consumer == null || consumer.Node == null)
                        {
                            use.Shared = true;
                            break;
                        }

                        if (inGroup.Contains(consumer.Node.ID + "\n" + consumer.Name) == false)
                            use.Shared = true;
                    }

                    if (use.Shared) break;
                }
            }
        }

        /// <summary>
        /// The field behind a port, found the way the graph itself pairs them: a port's name is
        /// its attribute's name, or the field's own name when that is not given. Editable is in
        /// there with Input and Output because that is what a constant node uses — String holds
        /// its text in a field marked [Editable("Value")], not in an output field.
        /// </summary>
        private static FieldInfo FindValueField(Type type, string portName)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var fields = current.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    if (field.FieldType != typeof(string)) continue;
                    if (NameOf(field) != portName) continue;

                    return field;
                }
            }

            return null;
        }

        private static string NameOf(FieldInfo field)
        {
            var input = field.GetCustomAttribute<InputAttribute>();
            if (input != null) return input.Name ?? field.Name;

            var output = field.GetCustomAttribute<OutputAttribute>();
            if (output != null) return output.Name ?? field.Name;

            var editable = field.GetCustomAttribute<EditableAttribute>();
            if (editable != null) return editable.Name ?? field.Name;

            return null;
        }

        private static bool TryFixedKey(Node node, out string key)
        {
            key = null;

            var type = node.GetType();
            if (DerivesFrom(type, FixedKeyBaseType) == false) return false;

            var method = type.GetMethod(FixedKeyMethod,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);

            if (method == null || method.ReturnType != typeof(string)) return false;

            try
            {
                key = method.Invoke(node, null) as string;
            }
            catch (Exception)
            {
                return false;
            }

            return string.IsNullOrEmpty(key) == false;
        }

        private static bool DerivesFrom(Type type, string baseTypeName)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
                if (current.Name == baseTypeName) return true;

            return false;
        }
    }
}
