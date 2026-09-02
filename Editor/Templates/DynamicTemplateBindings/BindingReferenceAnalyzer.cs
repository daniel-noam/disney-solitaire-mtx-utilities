using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlueGraph;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using SuperPlay.Domino.TemplatesBehavior.Runtime.Nodes;

namespace Utilities.Editor
{
    public readonly struct BindingIssueKey : IEquatable<BindingIssueKey>
    {
        public BindingListKind Kind { get; }
        public string Key { get; }

        public BindingIssueKey(BindingListKind kind, string key)
        {
            Kind = kind;
            Key = key ?? string.Empty;
        }

        // This is used as a dictionary key on every drawn row, so it needs real equality members:
        // the default ValueType.Equals falls back to reflection and GetHashCode only considers the
        // first field, which makes every lookup slow.
        public bool Equals(BindingIssueKey other) => Kind == other.Kind && Key == other.Key;

        public override bool Equals(object obj) => obj is BindingIssueKey other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ Key.GetHashCode();
    }

    /// <summary>One node that names a binding key, and the graph it sits in.</summary>
    public sealed class BindingSite
    {
        /// <summary>The graph holding the node, which is the one that has to be opened.</summary>
        public TemplateBehavior Graph { get; }

        public Node Node { get; }

        /// <summary>The subgraphs walked through to reach it, empty when it is in the graph itself.</summary>
        public string Path { get; }

        public BindingSite(TemplateBehavior graph, Node node, string path)
        {
            Graph = graph;
            Node = node;
            Path = path ?? string.Empty;
        }
    }

    /// <summary>The two issues that have one obvious remedy, and nothing else.</summary>
    public enum BindingFixKind
    {
        /// <summary>The graph uses this key and no binding declares it.</summary>
        AddMissingKey,
        /// <summary>This binding is declared and no node in the graph mentions it.</summary>
        RemoveOrphan,
    }

    /// <summary>
    /// One issue the inspector can act on, rather than only describe.
    ///
    /// Deliberately not every issue: a duplicate key and an unassigned value both need a decision
    /// only the person can make - which of the two to keep, what to put in the field - and a button
    /// that guesses at either is worse than no button.
    /// </summary>
    public sealed class BindingFix
    {
        public BindingFixKind Action { get; }
        public BindingListKind Kind { get; }

        /// <summary>The list's name as the summary writes it, e.g. "Segment Data".</summary>
        public string Category { get; }
        public string Key { get; }

        public BindingFix(BindingFixKind action, BindingListKind kind, string category, string key)
        {
            Action = action;
            Kind = kind;
            Category = category;
            Key = key;
        }
    }

    public enum BindingIssueSeverity
    {
        Warning,
        Error,
    }

    /// <summary>
    /// One finding, carrying its own severity and its own remedy if it has one.
    ///
    /// One per finding, not one per list: the summary used to join every finding into a single box,
    /// which had to pick one severity for all of them, so a warning shown beside an error was drawn
    /// as an error.
    /// </summary>
    public sealed class BindingIssue
    {
        public BindingIssueSeverity Severity { get; }
        public string Message { get; }

        /// <summary>The button this finding earns, or null when it needs a decision instead.</summary>
        public BindingFix Fix { get; }

        public BindingIssue(BindingIssueSeverity severity, string message, BindingFix fix = null)
        {
            Severity = severity;
            Message = message;
            Fix = fix;
        }
    }

    public sealed class BindingReferenceAnalysis
    {
        public IReadOnlyDictionary<string, int> SegmentDataRefCounts { get; }
        public IReadOnlyDictionary<string, int> LocalDataRefCounts { get; }
        public IReadOnlyDictionary<string, int> ObjectRefCounts { get; }
        public IReadOnlyDictionary<string, int> GroupRefCounts { get; }
        public IReadOnlyDictionary<string, int> AssetRefCounts { get; }

        public IReadOnlyDictionary<BindingIssueKey, string> InlineIssues { get; }
        public IReadOnlyList<BindingIssue> Issues { get; }

        /// <summary>
        /// Where each key is actually used, by key. The ref counts say how many; this says which,
        /// which is the difference between a report and somewhere to go.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<BindingSite>> Sites { get; }

        /// <summary>The nodes naming this key, newest-found first. Never null.</summary>
        public IReadOnlyList<BindingSite> SitesFor(string key) =>
            key != null && Sites.TryGetValue(key, out var sites) ? sites : EmptySites;

        private static readonly IReadOnlyList<BindingSite> EmptySites = new BindingSite[0];

        public bool HasIssues => Issues.Count > 0;
        public bool HasErrors => Issues.Any(issue => issue.Severity == BindingIssueSeverity.Error);
        public bool HasWarnings => Issues.Any(issue => issue.Severity == BindingIssueSeverity.Warning);

        public BindingReferenceAnalysis(
            IReadOnlyDictionary<string, int> segmentDataRefCounts,
            IReadOnlyDictionary<string, int> localDataRefCounts,
            IReadOnlyDictionary<string, int> objectRefCounts,
            IReadOnlyDictionary<string, int> groupRefCounts,
            IReadOnlyDictionary<string, int> assetRefCounts,
            IReadOnlyDictionary<BindingIssueKey, string> inlineIssues,
            IReadOnlyList<BindingIssue> issues,
            IReadOnlyDictionary<string, IReadOnlyList<BindingSite>> sites)
        {
            SegmentDataRefCounts = segmentDataRefCounts;
            LocalDataRefCounts = localDataRefCounts;
            ObjectRefCounts = objectRefCounts;
            GroupRefCounts = groupRefCounts;
            AssetRefCounts = assetRefCounts;
            InlineIssues = inlineIssues;
            Issues = issues;
            Sites = sites;
        }
    }

    public static class BindingReferenceAnalyzer
    {
        public static BindingReferenceAnalysis Analyze(DynamicTemplateBindings bindings, TemplateBehavior script)
        {
            var discoveredLiterals = new Dictionary<string, int>();
            var expectedSegmentData = new HashSet<string>();
            var expectedLocalData = new HashSet<string>();
            var expectedObjects = new HashSet<string>();
            var expectedGroups = new HashSet<string>();
            var expectedAssets = new HashSet<string>();
            bool hasDynamicNodes = false;
            var sites = new Dictionary<string, List<BindingSite>>();

            if (script != null)
            {
                hasDynamicNodes = ScanGraphLiterals(script, discoveredLiterals, sites, string.Empty,
                    expectedSegmentData, expectedLocalData, expectedObjects, expectedGroups, expectedAssets,
                    new HashSet<TemplateBehavior>());
            }

            var snapshot = new DynamicTemplateBindingsSnapshot(bindings);
            var segmentDataNames = snapshot.GetSegmentDataNames();
            var localDataNames = snapshot.GetLocalDataNames();
            var objectNames = snapshot.GetObjectNames();
            var groupNames = snapshot.GetGroupNames();
            var assetNames = snapshot.GetAssetNames();

            var segmentDataRefCounts = BuildSafeRefCounts(segmentDataNames, discoveredLiterals, hasDynamicNodes);
            var localDataRefCounts = BuildSafeRefCounts(localDataNames, discoveredLiterals, hasDynamicNodes);
            var objectRefCounts = BuildSafeRefCounts(objectNames, discoveredLiterals, hasDynamicNodes);
            var groupRefCounts = BuildSafeRefCounts(groupNames, discoveredLiterals, hasDynamicNodes);
            var assetRefCounts = BuildSafeRefCounts(assetNames, discoveredLiterals, hasDynamicNodes);

            var inlineIssues = new Dictionary<BindingIssueKey, string>();
            var issues = new List<BindingIssue>();

            CheckDuplicates(segmentDataNames, "Segment Data", BindingListKind.SegmentData, issues, inlineIssues);
            CheckDuplicates(localDataNames, "Local Data", BindingListKind.LocalData, issues, inlineIssues);
            CheckDuplicates(objectNames, "Object", BindingListKind.Object, issues, inlineIssues);
            CheckDuplicates(groupNames, "Group", BindingListKind.Group, issues, inlineIssues);
            CheckDuplicates(assetNames, "Asset", BindingListKind.Asset, issues, inlineIssues);

            ReportMissingFromBindings("Segment Data", BindingListKind.SegmentData, expectedSegmentData, segmentDataNames, issues);
            ReportMissingFromBindings("Local Data", BindingListKind.LocalData, expectedLocalData, localDataNames, issues);
            ReportMissingFromBindings("Object", BindingListKind.Object, expectedObjects, objectNames, issues);
            ReportMissingFromBindings("Group", BindingListKind.Group, expectedGroups, groupNames, issues);
            ReportMissingFromBindings("Asset", BindingListKind.Asset, expectedAssets, assetNames, issues);

            if (script != null)
            {
                ReportOrphans("Segment Data", BindingListKind.SegmentData, segmentDataNames, segmentDataRefCounts, issues, inlineIssues);
                ReportOrphans("Local Data", BindingListKind.LocalData, localDataNames, localDataRefCounts, issues, inlineIssues);
                ReportOrphans("Object", BindingListKind.Object, objectNames, objectRefCounts, issues, inlineIssues);
                ReportOrphans("Group", BindingListKind.Group, groupNames, groupRefCounts, issues, inlineIssues);
                ReportOrphans("Asset", BindingListKind.Asset, assetNames, assetRefCounts, issues, inlineIssues);
            }

            ReportMissingReferences("Object", BindingListKind.Object,
                snapshot.GetObjectMissingReferences(), issues, inlineIssues);
            ReportMissingReferences("Group", BindingListKind.Group,
                snapshot.GetGroupMissingReferences(), issues, inlineIssues);
            ReportMissingReferences("Asset", BindingListKind.Asset,
                snapshot.GetAssetMissingReferences(), issues, inlineIssues);

            if (bindings.GetComponent<DynamicTemplateBehavior>() == null)
                issues.Add(Error("DynamicTemplateBehavior component is missing on this GameObject."));
            else if (script == null)
                issues.Add(Error("No TemplateBehavior script assigned on DynamicTemplateBehavior."));

            return new BindingReferenceAnalysis(
                segmentDataRefCounts,
                localDataRefCounts,
                objectRefCounts,
                groupRefCounts,
                assetRefCounts,
                inlineIssues,
                issues,
                Freeze(sites));
        }

        private static bool ScanGraphLiterals(
            TemplateBehavior graph,
            Dictionary<string, int> discoveredLiterals,
            Dictionary<string, List<BindingSite>> sites,
            string path,
            HashSet<string> expectedSegmentData,
            HashSet<string> expectedLocalData,
            HashSet<string> expectedObjects,
            HashSet<string> expectedGroups,
            HashSet<string> expectedAssets,
            HashSet<TemplateBehavior> visitedGraphs)
        {
            // A subgraph that reaches itself would otherwise recurse until the Editor stack-overflows.
            if (graph == null || visitedGraphs.Add(graph) == false) return false;

            bool foundDynamicNode = false;

            // Reads a node's key literal, unless its input port is wired - in which case the key is
            // produced at runtime and the serialized literal is stale, so it must not be counted.
            void ScanNode(Node node, HashSet<string> expectedKeys, string[] memberNames)
            {
                if (IsKeyPortConnected(node, memberNames))
                {
                    foundDynamicNode = true;
                    return;
                }

                RegisterLiteral(ReadStringMember(node, memberNames), discoveredLiterals, expectedKeys,
                    sites, graph, node, path);
            }

            foreach (var node in graph.GetNodes<Node>())
            {
                if (node == null) continue;
                string typeName = node.GetType().Name;

                if (typeName.Contains("Concat") || typeName.Contains("Format") || typeName.Contains("Join"))
                {
                    foundDynamicNode = true;
                    continue;
                }

                if (node is IObjectBinding) ScanNode(node, expectedObjects, BindingNameMembers);
                else if (node is ILocalDataBinding) ScanNode(node, expectedLocalData, KeyMembers);
                else if (node is IGroupBinding) ScanNode(node, expectedGroups, BindingNameMembers);
                else if (node is IAssetBinding) ScanNode(node, expectedAssets, BindingNameMembers);
                else if (node is ISegmentDataBinding) ScanNode(node, expectedSegmentData, KeyMembers);
                else if (typeName == "PlayAudio" || typeName == "PlayAudioV2") ScanNode(node, expectedAssets, AudioClipMembers);
            }

            var initializeSpinner = graph.GetNode<InitializeSpinner>();
            if (initializeSpinner != null)
            {
                foreach (var segment in initializeSpinner.GetSegmentsData())
                    expectedSegmentData.Add(segment.name);
            }

            foreach (var subgraphNode in graph.GetNodes<SubgraphNode>())
            {
                if (subgraphNode != null && subgraphNode.SubGraph != null)
                {
                    bool subHasDynamic = ScanGraphLiterals(subgraphNode.SubGraph, discoveredLiterals,
                        sites, Join(path, subgraphNode.SubGraph.name),
                        expectedSegmentData, expectedLocalData, expectedObjects, expectedGroups, expectedAssets,
                        visitedGraphs);
                    if (subHasDynamic) foundDynamicNode = true;
                }
            }

            return foundDynamicNode;
        }

        private static void RegisterLiteral(string val, Dictionary<string, int> literalCounts,
            HashSet<string> expectedList, Dictionary<string, List<BindingSite>> sites,
            TemplateBehavior graph, Node node, string path)
        {
            if (string.IsNullOrEmpty(val) || (val.Contains("{") && val.Contains("}"))) return;
            literalCounts.TryGetValue(val, out var count);
            literalCounts[val] = count + 1;
            expectedList.Add(val);

            // Recorded here rather than in a second walk, so the sites can never disagree with the
            // count beside them: the same literal that is counted is the one that is remembered.
            if (!sites.TryGetValue(val, out var list))
            {
                list = new List<BindingSite>();
                sites[val] = list;
            }

            list.Add(new BindingSite(graph, node, path));
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<BindingSite>> Freeze(
            Dictionary<string, List<BindingSite>> sites)
        {
            var frozen = new Dictionary<string, IReadOnlyList<BindingSite>>(sites.Count);
            foreach (var pair in sites) frozen[pair.Key] = pair.Value;
            return frozen;
        }

        private static string Join(string path, string step) =>
            string.IsNullOrEmpty(path) ? step : path + " › " + step;

        /// <summary>
        /// Ref count per binding name, where -1 means "not referenced by any literal, but the graph has
        /// nodes that build keys at runtime, so we can't prove it is unused".
        /// </summary>
        private static Dictionary<string, int> BuildSafeRefCounts(IEnumerable<string> names, Dictionary<string, int> literalCounts, bool hasDynamicNodes)
        {
            var dict = new Dictionary<string, int>();
            if (names == null) return dict;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name) || dict.ContainsKey(name)) continue;

                literalCounts.TryGetValue(name, out var count);
                dict[name] = count == 0 && hasDynamicNodes ? -1 : count;
            }
            return dict;
        }

        private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Candidate member names holding a binding key, most-preferred first. Properties win over fields
        // because the interfaces (IBinding.BindingName, ISegmentDataBinding.Key) expose the resolved value.
        private static readonly string[] BindingNameMembers = { "BindingName", "bindingName", "_bindingName" };
        private static readonly string[] KeyMembers = { "Key", "key", "_key" };
        private static readonly string[] AudioClipMembers = { "clipName", "_clipName", "audioName", "soundName", "bindingName" };

        // Node members are resolved by reflection for every node of every pass and the result only
        // depends on the type, so both lookups are cached for the lifetime of the domain.
        private static readonly Dictionary<(Type, string), MemberInfo> StringMemberCache = new Dictionary<(Type, string), MemberInfo>();
        private static readonly Dictionary<(Type, string), string> InputPortNameCache = new Dictionary<(Type, string), string>();

        /// <summary>Reads the first of <paramref name="memberNames"/> that exists on the node as a string.</summary>
        private static string ReadStringMember(Node node, string[] memberNames)
        {
            if (node == null) return string.Empty;
            var type = node.GetType();

            foreach (var name in memberNames)
            {
                switch (GetStringMember(type, name))
                {
                    case PropertyInfo prop: return prop.GetValue(node) as string;
                    case FieldInfo field: return field.GetValue(node) as string;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// True when the node's key input is fed by a wire rather than typed in.
        /// <para>
        /// The port is resolved from the <see cref="InputAttribute"/> on the backing field, because port
        /// names are display names that do not follow the member name: the key of an object binding lives
        /// on a port called "Name", an audio clip on "Audio Clip Name". Deriving the port name from the
        /// member name instead never matched those, which let stale literals count as live references.
        /// </para>
        /// </summary>
        private static bool IsKeyPortConnected(Node node, string[] memberNames)
        {
            if (node == null || node.Ports == null) return false;
            var type = node.GetType();

            foreach (var name in memberNames)
            {
                var portName = GetInputPortName(type, name);
                if (portName == null) continue;

                var port = node.GetPort(portName);
                return port != null && port.ConnectionCount > 0;
            }
            return false;
        }

        /// <summary>The port name declared by the <see cref="InputAttribute"/> on a string field, or null.</summary>
        private static string GetInputPortName(Type type, string fieldName)
        {
            var cacheKey = (type, fieldName);
            if (InputPortNameCache.TryGetValue(cacheKey, out var cached))
                return cached;

            string portName = null;

            var field = type.GetField(fieldName, MemberFlags);
            if (field != null && field.FieldType == typeof(string))
            {
                var input = field.GetCustomAttribute<InputAttribute>();
                // BlueGraph names the port after the attribute, falling back to the field name.
                if (input != null) portName = input.Name ?? field.Name;
            }

            InputPortNameCache[cacheKey] = portName;
            return portName;
        }

        private static MemberInfo GetStringMember(Type type, string name)
        {
            var cacheKey = (type, name);
            if (StringMemberCache.TryGetValue(cacheKey, out var cached))
                return cached;

            MemberInfo member = null;

            var prop = type.GetProperty(name, MemberFlags);
            if (prop != null && prop.PropertyType == typeof(string))
            {
                member = prop;
            }
            else
            {
                var field = type.GetField(name, MemberFlags);
                if (field != null && field.FieldType == typeof(string)) member = field;
            }

            StringMemberCache[cacheKey] = member;
            return member;
        }

        private static BindingIssue Error(string message, BindingFix fix = null) =>
            new BindingIssue(BindingIssueSeverity.Error, message, fix);

        private static BindingIssue Warning(string message, BindingFix fix = null) =>
            new BindingIssue(BindingIssueSeverity.Warning, message, fix);

        private static void ReportMissingFromBindings(string category, BindingListKind kind, HashSet<string> expectedKeys, IEnumerable<string> bindingNames, List<BindingIssue> issues)
        {
            if (expectedKeys == null || expectedKeys.Count == 0) return;

            var bindingSet = bindingNames?.Where(name => !string.IsNullOrEmpty(name)).ToHashSet() ?? new HashSet<string>();
            var missing = new List<string>();

            foreach (var key in expectedKeys)
            {
                if (bindingSet.Contains(key) || (key.Contains("{") && key.Contains("}"))) continue;
                missing.Add(key);
            }

            foreach (var key in missing.OrderBy(k => k))
                issues.Add(Error(
                    $"{category} key \"{key}\" is used in the graph but missing from bindings.",
                    new BindingFix(BindingFixKind.AddMissingKey, kind, category, key)));
        }

        private const string UnassignedInlineMessage = "Missing reference (value not assigned)";
        private const string BrokenInlineMessage = "Missing reference (what it pointed at was deleted)";

        private static void ReportMissingReferences(string category, BindingListKind kind, IReadOnlyList<MissingReferenceEntry> missingEntries, List<BindingIssue> issues, Dictionary<BindingIssueKey, string> inlineIssues)
        {
            if (missingEntries == null || missingEntries.Count == 0) return;

            foreach (var entry in missingEntries)
            {
                var broken = entry.State == ReferenceState.Broken;

                // A broken reference is worth its own words: an empty field is work not done yet,
                // while this one was done and something else undid it - usually a deleted asset,
                // which is the thing you would want to know about before a build finds out.
                var fault = broken
                    ? "has a missing reference; what it pointed at has been deleted."
                    : "has no value assigned.";

                if (string.IsNullOrEmpty(entry.Name))
                {
                    // Unnamed entries can't be keyed inline (their empty key already shows a "Key is
                    // empty" issue), so they are named by their list position instead.
                    issues.Add(Error($"{category} entry #{entry.Index + 1} {fault}"));
                    continue;
                }

                var inlineMessage = broken ? BrokenInlineMessage : UnassignedInlineMessage;
                var issueKey = new BindingIssueKey(kind, entry.Name);
                if (inlineIssues.TryGetValue(issueKey, out var existing) && string.IsNullOrEmpty(existing) == false)
                    inlineIssues[issueKey] = $"{existing} • {inlineMessage}";
                else
                    inlineIssues[issueKey] = inlineMessage;

                issues.Add(Error($"{category} \"{entry.Name}\" {fault}"));
            }
        }

        private static void ReportOrphans(string category, BindingListKind kind, IEnumerable<string> bindingNames, Dictionary<string, int> refCounts, List<BindingIssue> issues, Dictionary<BindingIssueKey, string> inlineIssues)
        {
            if (bindingNames == null) return;

            foreach (var name in bindingNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (!refCounts.TryGetValue(name, out var count)) continue;

                if (count == -1)
                {
                    inlineIssues[new BindingIssueKey(kind, name)] = "Potentially handled dynamically in graph";
                    continue;
                }

                if (count != 0) continue;

                // Every orphan gets its own entry now, whichever way it is also marked: the box is
                // where the button lives, and a button behind a hover tooltip is no button at all.
                issues.Add(Warning($"{category} key \"{name}\" has no graph references.",
                    new BindingFix(BindingFixKind.RemoveOrphan, kind, category, name)));

                if (kind.SupportsInlineIssues())
                    inlineIssues[new BindingIssueKey(kind, name)] = "No graph references";
            }
        }

        private static void CheckDuplicates(IEnumerable<string> names, string category, BindingListKind kind, List<BindingIssue> issues, Dictionary<BindingIssueKey, string> inlineIssues)
        {
            if (names == null) return;
            var seen = new HashSet<string>();
            var reportedDuplicates = new HashSet<string>();
            var hasEmptyKey = false;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                {
                    if (hasEmptyKey == false)
                    {
                        hasEmptyKey = true;
                        issues.Add(Error($"{category} has an entry with an empty key."));

                        if (kind.SupportsInlineIssues())
                            inlineIssues[new BindingIssueKey(kind, string.Empty)] = "Key is empty";
                    }
                    continue;
                }

                if (seen.Add(name) == false)
                {
                    if (reportedDuplicates.Add(name))
                        issues.Add(Error($"{category} has duplicate key \"{name}\"."));

                    if (kind.SupportsInlineIssues())
                        inlineIssues[new BindingIssueKey(kind, name)] = "Duplicate key in list";
                }
            }
        }
    }
}