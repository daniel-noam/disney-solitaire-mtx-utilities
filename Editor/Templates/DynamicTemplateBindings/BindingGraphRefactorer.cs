using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using BlueGraph;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using SuperPlay.Domino.TemplatesBehavior.Runtime.Nodes;

namespace Tools.Editor.EditorUtilities
{
    public static class BindingGraphRefactorer
    {
        public static void RenameReferences(TemplateBehavior graph, BindingListKind kind, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || oldName == newName)
                return;

            RenameReferences(graph, kind, new Dictionary<string, string> { [oldName] = newName });
        }

        /// <summary>
        /// Applies every old-name to new-name mapping in a single walk of the graph and its subgraphs.
        /// Callers that rename a group of keys at once must pass them all here - renaming only some of
        /// them leaves the graph pointing at keys the bindings no longer define.
        /// </summary>
        public static void RenameReferences(TemplateBehavior graph, BindingListKind kind, IReadOnlyDictionary<string, string> renames)
        {
            if (graph == null || renames == null || renames.Count == 0)
                return;

            var touchedGraphs = new HashSet<TemplateBehavior>();
            ScanAndRename(graph, kind, renames, touchedGraphs);

            // Only the graphs this rename walked - a blanket SaveAssets() would also commit unrelated
            // dirty assets the user has open.
            foreach (var touchedGraph in touchedGraphs)
                AssetDatabase.SaveAssetIfDirty(touchedGraph);
        }

        private static void ScanAndRename(TemplateBehavior graph, BindingListKind kind, IReadOnlyDictionary<string, string> renames, HashSet<TemplateBehavior> visitedGraphs)
        {
            // A subgraph that reaches itself would otherwise recurse until the Editor stack-overflows.
            if (graph == null || visitedGraphs.Add(graph) == false) return;

            Undo.RegisterCompleteObjectUndo(graph, "Rename Binding References");

            switch (kind)
            {
                case BindingListKind.Object:
                    foreach (var node in graph.GetNodes<IObjectBinding>())
                        TrySetStringValue(node, renames, "BindingName", "bindingName", "_bindingName");
                    break;

                case BindingListKind.LocalData:
                    foreach (var node in graph.GetNodes<ILocalDataBinding>())
                        TrySetStringValue(node, renames, "Key", "key", "_key");
                    break;

                case BindingListKind.Group:
                    foreach (var node in graph.GetNodes<IGroupBinding>())
                        TrySetStringValue(node, renames, "BindingName", "bindingName", "_bindingName");
                    break;

                case BindingListKind.Asset:
                    foreach (var node in graph.GetNodes<IAssetBinding>())
                        TrySetStringValue(node, renames, "BindingName", "bindingName", "_bindingName");

                    // Audio clip names are asset bindings too, but these nodes don't implement IAssetBinding.
                    foreach (var node in graph.GetNodes<Node>())
                    {
                        if (node == null) continue;

                        string typeName = node.GetType().Name;
                        if (typeName == "PlayAudio" || typeName == "PlayAudioV2")
                            TrySetStringValue(node, renames, "clipName", "_clipName", "audioName", "soundName", "bindingName");
                    }
                    break;

                case BindingListKind.SegmentData:
                    foreach (var node in graph.GetNodes<ISegmentDataBinding>())
                        TrySetStringValue(node, renames, "Key", "key", "_key");

                    var initializeSpinner = graph.GetNode<InitializeSpinner>();
                    if (initializeSpinner != null)
                        RefactorSpinnerCollections(initializeSpinner, renames);
                    break;
            }

            EditorUtility.SetDirty(graph);

            foreach (var subgraphNode in graph.GetNodes<SubgraphNode>())
            {
                if (subgraphNode != null && subgraphNode.SubGraph != null)
                    ScanAndRename(subgraphNode.SubGraph, kind, renames, visitedGraphs);
            }
        }

        private static void RefactorSpinnerCollections(object spinner, IReadOnlyDictionary<string, string> renames)
        {
            if (spinner == null) return;

            var fields = spinner.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;

                if (fieldType.IsArray)
                {
                    var array = (Array)field.GetValue(spinner);
                    if (array == null) continue;
                    for (int i = 0; i < array.Length; i++)
                    {
                        var element = array.GetValue(i);
                        if (element == null) continue;
                        if (TryModifyFieldsOrProperties(ref element, renames))
                        {
                            array.SetValue(element, i);
                        }
                    }
                }
                else if (typeof(System.Collections.IList).IsAssignableFrom(fieldType))
                {
                    var list = (System.Collections.IList)field.GetValue(spinner);
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var element = list[i];
                        if (element == null) continue;
                        if (TryModifyFieldsOrProperties(ref element, renames))
                        {
                            list[i] = element;
                        }
                    }
                }
            }
        }

        private static bool TryModifyFieldsOrProperties(ref object target, IReadOnlyDictionary<string, string> renames)
        {
            if (target == null) return false;
            Type type = target.GetType();
            bool modified = false;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(string)) continue;

                if (TryGetRename(renames, field.GetValue(target) as string, out var newVal))
                {
                    field.SetValue(target, newVal);
                    modified = true;
                }
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (!prop.CanWrite || prop.PropertyType != typeof(string)) continue;

                if (TryGetRename(renames, prop.GetValue(target) as string, out var newVal))
                {
                    prop.SetValue(target, newVal);
                    modified = true;
                }
            }

            return modified;
        }

        private static void TrySetStringValue(object target, IReadOnlyDictionary<string, string> renames, params string[] identifierNames)
        {
            if (target == null) return;
            Type type = target.GetType();

            foreach (var name in identifierNames)
            {
                PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) continue;

                if (TryGetRename(renames, prop.GetValue(target) as string, out var newVal))
                {
                    prop.SetValue(target, newVal);
                    return;
                }
            }

            foreach (var name in identifierNames)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(string)) continue;

                if (TryGetRename(renames, field.GetValue(target) as string, out var newVal))
                {
                    field.SetValue(target, newVal);
                    return;
                }
            }
        }

        private static bool TryGetRename(IReadOnlyDictionary<string, string> renames, string currentValue, out string newValue)
        {
            newValue = null;
            return string.IsNullOrEmpty(currentValue) == false && renames.TryGetValue(currentValue, out newValue);
        }
    }
}
