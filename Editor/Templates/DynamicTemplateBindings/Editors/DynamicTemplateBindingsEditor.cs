using System.Collections.Generic;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    [CustomEditor(typeof(DynamicTemplateBindings))]
    public class DynamicTemplateBindingsEditor : UnityEditor.Editor
    {
        /// <summary>How often the connected script is polled for external changes, in seconds.</summary>
        private const double ScriptPollInterval = 0.5;

        /// <summary>How many issues the summary shows before the rest go behind the foldout.</summary>
        private const int SummaryLinesShown = 4;

        /// <summary>The severity icon inside an issue box, at a row's height rather than a HelpBox's.</summary>
        private const float IssueIconSize = 16f;

        private BindingReferenceAnalysis _analysis;
        private TemplateBehavior _cachedScript;
        private bool _analysisStale = true;
        private bool _showAllIssues;

        // Applied once the pass is over: acting on a button that adds or removes a list entry part
        // way through drawing that list is how an inspector ends up drawing a row that is gone.
        private BindingFix _pendingFix;
        private bool _wasScriptDirty;

        private DynamicTemplateBehavior _cachedBehavior;
        private SerializedObject _behaviorSerializedObject;
        private double _nextScriptPollTime;

        private void OnEnable()
        {
            EditorApplication.projectChanged += MarkAnalysisStale;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.contextualPropertyMenu += OnPropertyContextMenu;

            BindingReferenceDrawerContext.OnContextModified += MarkAnalysisStale;
            Undo.undoRedoPerformed += MarkAnalysisStale;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= MarkAnalysisStale;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.contextualPropertyMenu -= OnPropertyContextMenu;

            BindingReferenceDrawerContext.OnContextModified -= MarkAnalysisStale;
            Undo.undoRedoPerformed -= MarkAnalysisStale;

            BindingReferenceDrawerContext.Clear();

            _cachedBehavior = null;
            _behaviorSerializedObject = null;
        }

        private void OnPropertyContextMenu(GenericMenu menu, SerializedProperty property)
        {
            if (property == null || property.serializedObject == null) return;
            if (property.name != "name") return;

            // This editor's own component, not merely any bindings component: the callback is
            // global, so every open bindings inspector runs it and they would otherwise each add
            // their own copy of the entry — and each drive the connected-script cache off the
            // component they are not looking at.
            var owner = property.serializedObject.targetObject as DynamicTemplateBindings;
            if (owner == null || owner != target) return;

            string path = property.propertyPath;
            BindingListKind kind;

            if (path.Contains("_segmentData")) kind = BindingListKind.SegmentData;
            else if (path.Contains("_localData")) kind = BindingListKind.LocalData;
            else if (path.Contains("_objects")) kind = BindingListKind.Object;
            else if (path.Contains("_groups")) kind = BindingListKind.Group;
            else if (path.Contains("_assets")) kind = BindingListKind.Asset;
            else return;

            string key = property.stringValue;

            // Read straight off the component rather than out of the drawer context: renaming
            // rewrites graph nodes, which has nothing to do with whether the analysis is running.
            var currentScript = GetConnectedScript(owner);

            menu.AddSeparator("");

            AddShowNodesItem(menu, key);

            if (!DynamicTemplateBindingsSettings.Instance.renameMenuItem) return;

            if (currentScript != null && !string.IsNullOrEmpty(key))
            {
                var propertyCopy = property.Copy();
                menu.AddItem(new GUIContent("Rename key and graph references"), false, () =>
                {
                    // Clean direct layout execution call (Reflection references completely removed)
                    RenameBindingWindow.Open(kind, key, propertyCopy, currentScript);
                });
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Rename key and graph references (Requires active Graph & non-empty key)"));
            }
        }

        /// <summary>
        /// The other half of the reference count: it says a key is used three times, and this goes
        /// to the three.
        ///
        /// Gated by the analysis rather than by a setting of its own — the sites come out of the
        /// same walk as the counts, so with the analysis off there is nothing to offer and nothing
        /// to explain.
        /// </summary>
        private void AddShowNodesItem(GenericMenu menu, string key)
        {
            if (_analysis == null || string.IsNullOrEmpty(key)) return;

            var sites = _analysis.SitesFor(key);
            var label = BindingKeyNavigator.Describe(key, sites);
            if (label == null) return;

            menu.AddItem(new GUIContent(label), false, () => BindingKeyNavigator.Show(sites, key));
        }

        private void OnEditorUpdate()
        {
            if (target == null)
                return;

            // The poll exists to notice the graph changing under an analysis. With no analysis
            // running there is nothing to keep fresh.
            if (!DynamicTemplateBindingsSettings.Instance.AnalysesReferences)
                return;

            // This runs on every editor tick, so the poll is throttled rather than reading the
            // connected behaviour hundreds of times a second.
            if (EditorApplication.timeSinceStartup < _nextScriptPollTime)
                return;

            _nextScriptPollTime = EditorApplication.timeSinceStartup + ScriptPollInterval;

            var script = GetConnectedScript((DynamicTemplateBindings)target);
            var isScriptDirty = script != null && EditorUtility.IsDirty(script);

            if (script != _cachedScript || isScriptDirty != _wasScriptDirty)
            {
                _analysisStale = true;
                _wasScriptDirty = isScriptDirty;
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            // Before anything reads a style. An inspector has no window of its own to have built
            // them, and after a domain reload they are released — so the first repaint of a
            // bindings component with no tool window ever opened would draw with null styles.
            ToolStyles.Ensure();

            serializedObject.Update();

            var settings = DynamicTemplateBindingsSettings.Instance;
            var bindings = (DynamicTemplateBindings)target;
            var script = GetConnectedScript(bindings);

            if (!settings.AnalysesReferences)
            {
                // Dropped rather than kept and ignored, so switching the analysis back on cannot
                // show a report of whatever the graph looked like when it was switched off.
                _analysis = null;
                _cachedScript = script;
            }
            else if (ShouldRefreshAnalysis(script))
            {
                RefreshAnalysis(bindings, script);
            }

            if (settings.showSummary && _analysis != null && _analysis.HasIssues)
            {
                DrawAnalysisSummary();
                EditorGUILayout.Space(4f);
            }

            BindingReferenceDrawerContext.Set(_analysis, script);
            
            EditorGUI.BeginChangeCheck();
            try
            {
                DrawDefaultInspector();
            }
            finally
            {
                BindingReferenceDrawerContext.Clear();
            }

            if (EditorGUI.EndChangeCheck() || serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
                if (settings.AnalysesReferences) RefreshAnalysis(bindings, script);
                Repaint();
            }

            ApplyPendingFix();
        }

        private TemplateBehavior GetConnectedScript(DynamicTemplateBindings bindings)
        {
            var behavior = bindings.GetComponent<DynamicTemplateBehavior>();
            if (behavior == null)
            {
                _cachedBehavior = null;
                _behaviorSerializedObject = null;
                return null;
            }

            // Called from both the repaint path and the update poll, so the SerializedObject is reused
            // instead of allocated per call.
            if (behavior != _cachedBehavior || _behaviorSerializedObject == null)
            {
                _cachedBehavior = behavior;
                _behaviorSerializedObject = new SerializedObject(behavior);
            }
            else
            {
                _behaviorSerializedObject.Update();
            }

            var scriptProperty = _behaviorSerializedObject.FindProperty("_script");
            return scriptProperty == null ? null : scriptProperty.objectReferenceValue as TemplateBehavior;
        }

        private bool ShouldRefreshAnalysis(TemplateBehavior script)
        {
            if (_analysisStale)
                return true;

            if (script != _cachedScript)
                return true;

            if (script != null && EditorUtility.IsDirty(script))
                return true;

            return false;
        }

        private void RefreshAnalysis(DynamicTemplateBindings bindings, TemplateBehavior script)
        {
            if (script != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(script);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    // Forces the graph's node sub-assets to be loaded; the analyzer walks them by reference.
                    AssetDatabase.LoadAllAssetsAtPath(assetPath);
                }
            }

            _analysis = BindingReferenceAnalyzer.Analyze(bindings, script);
            _cachedScript = script;
            _analysisStale = false;
            _wasScriptDirty = script != null && EditorUtility.IsDirty(script);
        }

        /// <summary>
        /// One box per finding, capped so a graph with a lot of broken keys does not push the
        /// bindings themselves off the screen.
        ///
        /// A box each rather than one box of bullets: a single HelpBox has one icon and one
        /// severity for everything in it, so a warning listed beside an error was drawn as an
        /// error. A box also has somewhere to put the button that puts the finding right.
        ///
        /// Capped rather than truncated: these are the list of things to go and fix, so the rest
        /// stay one click away instead of being thrown away.
        /// </summary>
        private void DrawAnalysisSummary()
        {
            if (_analysis == null) return;

            var issues = _analysis.Issues;
            if (issues == null || issues.Count == 0) return;

            var overflowing = issues.Count > SummaryLinesShown;
            var shown = overflowing && !_showAllIssues ? SummaryLinesShown : issues.Count;

            for (var i = 0; i < shown; i++)
            {
                DrawIssue(issues[i]);
                GUILayout.Space(ToolStyles.SpaceXS);
            }

            if (overflowing)
                _showAllIssues = EditorGUILayout.Foldout(_showAllIssues, $"All {issues.Count} issues", true);
        }

        /// <summary>
        /// The icon, what is wrong, and the one button that fixes it if it has one.
        ///
        /// The icon is the console's small variant at the row's own height. A HelpBox draws its
        /// icon at nearly three lines tall, which in a stack of boxes is most of what you see.
        /// </summary>
        private void DrawIssue(BindingIssue issue)
        {
            using (new EditorGUILayout.HorizontalScope(ToolStyles.Inset))
            {
                var icon = EditorGUIUtility.IconContent(issue.Severity == BindingIssueSeverity.Error
                    ? "console.erroricon.sml"
                    : "console.warnicon.sml");

                GUILayout.Label(icon, GUILayout.Width(IssueIconSize), GUILayout.Height(IssueIconSize));
                GUILayout.Space(ToolStyles.SpaceS);

                // No FlexibleSpace: the message is the element that expands, so it wraps into the
                // width the button leaves rather than being squeezed to its content.
                GUILayout.Label(issue.Message, ToolStyles.Hint, GUILayout.ExpandWidth(true));

                if (issue.Fix == null) return;

                var add = issue.Fix.Action == BindingFixKind.AddMissingKey;
                var tooltip = add
                    ? $"Add a {issue.Fix.Category} binding for \"{issue.Fix.Key}\"."
                    : $"Remove the {issue.Fix.Category} binding \"{issue.Fix.Key}\".";

                GUILayout.Space(ToolStyles.SpaceS);
                if (GUILayout.Button(new GUIContent(add ? "Add" : "Remove", tooltip), ToolStyles.Secondary,
                        GUILayout.Width(ToolStyles.ButtonS), GUILayout.Height(ToolStyles.ControlHeight)))
                    _pendingFix = issue.Fix;
            }
        }

        private void ApplyPendingFix()
        {
            if (_pendingFix == null) return;

            var fix = _pendingFix;
            _pendingFix = null;

            if (!BindingFixer.Apply(serializedObject, fix)) return;

            Undo.SetCurrentGroupName(BindingFixer.Describe(fix));
            serializedObject.ApplyModifiedProperties();

            _analysisStale = true;
            Repaint();
        }

        private void MarkAnalysisStale()
        {
            _analysisStale = true;
            Repaint();
        }

    }
}