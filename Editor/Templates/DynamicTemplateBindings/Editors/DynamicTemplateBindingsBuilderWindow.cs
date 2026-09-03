using System.Collections.Generic;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Adds a run of binding keys in one go: Reward_Panel_0 and a count of 60 rather than sixty
    /// presses of + and sixty typed names.
    ///
    /// A window rather than more buttons in the inspector, because the whole job is a preview you
    /// read before committing - which key it starts at, how many, and which of them the template
    /// already has.
    /// </summary>
    public class DynamicTemplateBindingsBuilderWindow : EditorWindow
    {
        private static readonly Vector2 MinWindowSize = new Vector2(460, 520);

        private const float PreviewHeight = 190f;

        private DynamicTemplateBindings target;
        private SerializedObject serialized;

        private BindingListKind kind = BindingListKind.SegmentData;
        private string pattern = "Reward_Panel_{n}";
        private int first;
        private int count = 10;
        private int typeIndex;
        private bool required = true;

        private Vector2 previewScroll;

        // Frozen for the pass: the preview and the button have to agree about what is going to
        // happen, and the selection can change under a repaint.
        private List<string> frameKeys = new List<string>();
        private HashSet<string> frameExisting = new HashSet<string>();
        private List<string> frameStale = new List<string>();
        private List<string> frameMatching = new List<string>();
        private int frameNew;

        /// <summary>The 2000 band, with the other template tools. See DESIGN.md.</summary>
        private const int TemplateToolPriority = 2001;

        [MenuItem("Utilities/Dynamic Template Bindings Builder", false, TemplateToolPriority)]
        public static void ShowWindow()
        {
            GetWindow<DynamicTemplateBindingsBuilderWindow>("Dynamic Template Bindings Builder")
                .minSize = MinWindowSize;
        }

        private void OnEnable()
        {
            // Without this, hover states only repaint when something else happens to trigger a frame.
            wantsMouseMove = true;
            AdoptSelection();
        }

        private void OnSelectionChange()
        {
            AdoptSelection();
            Repaint();
        }

        /// <summary>
        /// Follows the selection, but never clears a target that is already set: picking something
        /// else in the Project window to look at should not empty the form you were filling in.
        /// </summary>
        private void AdoptSelection()
        {
            var picked = Selection.activeGameObject == null
                ? null
                : Selection.activeGameObject.GetComponent<DynamicTemplateBindings>();

            if (picked != null && picked != target) SetTarget(picked);
        }

        private void SetTarget(DynamicTemplateBindings bindings)
        {
            target = bindings;
            serialized = bindings == null ? null : new SerializedObject(bindings);
            typeIndex = 0;
        }

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            if (target == null && serialized != null) SetTarget(null);
            if (serialized != null) serialized.Update();

            FreezeFrame();

            GUILayout.Space(ToolStyles.SpaceL);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawTargetCard();
                    GUILayout.Space(ToolStyles.SpaceM);
                    DrawPatternCard();
                    GUILayout.Space(ToolStyles.SpaceM);
                    DrawPreviewCard();
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }
            GUILayout.Space(ToolStyles.SpaceL);
        }

        /// <summary>
        /// One snapshot of what would be added, taken before anything is drawn. The preview, the
        /// count and the button all read from this, so they cannot describe different things.
        /// </summary>
        private void FreezeFrame()
        {
            frameKeys = BindingKeyPattern.Expand(pattern, first, count);
            frameExisting = ExistingKeys();

            frameNew = 0;
            foreach (var key in frameKeys)
                if (!frameExisting.Contains(key)) frameNew++;

            // Everything of this pattern's family already in the list, and the part of it the run
            // does not cover — what a shorter run leaves behind, which is what Replace is for.
            var wanted = new HashSet<string>(frameKeys);
            frameMatching.Clear();
            frameStale.Clear();
            foreach (var key in frameExisting)
            {
                if (!BindingKeyPattern.Matches(pattern, key)) continue;

                frameMatching.Add(key);
                if (!wanted.Contains(key)) frameStale.Add(key);
            }
        }

        private HashSet<string> ExistingKeys()
        {
            var keys = new HashSet<string>();
            var list = serialized?.FindProperty(kind.ArrayPath());
            if (list == null || !list.isArray) return keys;

            for (var i = 0; i < list.arraySize; i++)
            {
                var name = list.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (name != null && !string.IsNullOrEmpty(name.stringValue)) keys.Add(name.stringValue);
            }

            return keys;
        }

        private void DrawTargetCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Template");

                var picked = (DynamicTemplateBindings) EditorGUILayout.ObjectField(
                    new GUIContent("Bindings", "The component the keys are added to. Selecting a " +
                                               "GameObject that has one picks it up automatically."),
                    target, typeof(DynamicTemplateBindings), true);
                if (picked != target) SetTarget(picked);
            }
        }

        private void DrawPatternCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Keys to add");

                var pickedKind = (BindingListKind) EditorGUILayout.EnumPopup(
                    new GUIContent("List", "Which of the binding lists the keys are added to."), kind);
                if (pickedKind != kind)
                {
                    kind = pickedKind;
                    typeIndex = 0;
                }

                pattern = EditorGUILayout.TextField(
                    new GUIContent("Pattern", "{n} is where the number goes, and it can go anywhere " +
                                              "in the name. Repeat the n to pad it: {nn} counts 00, " +
                                              "01, 02."),
                    pattern);

                first = EditorGUILayout.IntField(
                    new GUIContent("First", "The number the run starts at."), first);
                count = Mathf.Clamp(EditorGUILayout.IntField(
                        new GUIContent("Count", $"How many to add, up to {BindingKeyPattern.MaxCount}."),
                        count),
                    0, BindingKeyPattern.MaxCount);

                DrawTypeFields();
            }
        }

        /// <summary>
        /// The fields the chosen list actually has. Read off the list itself rather than hardcoded
        /// per kind, so a binding type that gains or loses a field does not leave this lying.
        /// </summary>
        private void DrawTypeFields()
        {
            var names = BindingFixer.TypeNames(kind);
            if (names.Length == 0)
            {
                GUILayout.Label($"{kind.DisplayName()} bindings have no Type to set.", ToolStyles.Hint);
                return;
            }

            typeIndex = EditorGUILayout.Popup(
                new GUIContent("Type", "Applied to every key in the run."),
                Mathf.Clamp(typeIndex, 0, names.Length - 1), names);

            if (kind == BindingListKind.SegmentData)
                required = EditorGUILayout.Toggle(
                    new GUIContent("Required", "Applied to every key in the run."), required);
        }

        private void DrawPreviewCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Preview");

                using (var scroll = new EditorGUILayout.ScrollViewScope(previewScroll, ToolStyles.Inset,
                           GUILayout.Height(PreviewHeight)))
                {
                    previewScroll = scroll.scrollPosition;

                    // The one requirement worth stating, and only when it is what is missing: a
                    // pattern with no slot would otherwise just preview as nothing at all.
                    if (frameKeys.Count == 0)
                        GUILayout.Label(
                            string.IsNullOrWhiteSpace(pattern) ||
                            BindingKeyPattern.TryParse(pattern, out _, out _, out _)
                                ? "Nothing to add yet."
                                : "Put {n} in the pattern, where the number goes.",
                            ToolStyles.Hint);

                    foreach (var key in frameKeys)
                    {
                        var exists = frameExisting.Contains(key);
                        ToolStyles.ColouredLabel(exists ? key + "   already there" : key,
                            ToolStyles.MonoSmall, exists ? ToolStyles.Faint : ToolStyles.Text);
                    }
                }

                GUILayout.Space(ToolStyles.SpaceS);
                DrawActions();
            }
        }

        private void DrawActions()
        {
            var skipped = frameKeys.Count - frameNew;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new ToolStyles.DisabledScope(target == null || frameNew == 0))
                {
                    if (GUILayout.Button(new GUIContent($"Add {frameNew}",
                                "Append every key that is not already in the list, and touch nothing else."),
                            ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonM),
                            GUILayout.Height(ToolStyles.ActionHeight)))
                        Apply(BuildAction.Add);
                }

                using (new ToolStyles.DisabledScope(target == null || (frameNew == 0 && frameStale.Count == 0)))
                {
                    if (GUILayout.Button(new GUIContent("Replace",
                                "Leave the list holding exactly this run: adds what is missing, and " +
                                "deletes the keys of the same pattern that the run does not reach."),
                            ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonM),
                            GUILayout.Height(ToolStyles.ActionHeight)))
                        Apply(BuildAction.Replace);
                }

                using (new ToolStyles.DisabledScope(target == null || frameMatching.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent($"Remove {frameMatching.Count}",
                                "Delete every key of this pattern, the run included. First and Count " +
                                "make no difference to this one."),
                            ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonM),
                            GUILayout.Height(ToolStyles.ActionHeight)))
                        Apply(BuildAction.Remove);
                }

                GUILayout.Space(ToolStyles.SpaceM);
                GUILayout.Label(Situation(skipped), ToolStyles.Hint);
                GUILayout.FlexibleSpace();
            }
        }

        /// <summary>
        /// What the two buttons would do differently, in one line. Only the parts that apply, so
        /// the row says nothing at all when there is nothing to warn about.
        /// </summary>
        private string Situation(int skipped)
        {
            var parts = new List<string>();
            if (skipped > 0) parts.Add($"{skipped} already there");
            if (frameStale.Count > 0) parts.Add($"{frameStale.Count} outside the run, which Replace deletes");

            return parts.Count == 0 ? " " : string.Join("  ·  ", parts);
        }

        private enum BuildAction
        {
            /// <summary>Append what is missing and touch nothing else.</summary>
            Add,
            /// <summary>Leave the list holding exactly the run.</summary>
            Replace,
            /// <summary>Take the whole pattern out, run included.</summary>
            Remove,
        }

        private void Apply(BuildAction action)
        {
            if (serialized == null) return;

            // Removals first: an added key would otherwise be a candidate for the delete that
            // follows it, since deleting works by name.
            var removed = 0;
            if (action != BuildAction.Add)
            {
                foreach (var key in action == BuildAction.Remove ? frameMatching : frameStale)
                    if (BindingFixer.RemoveKey(serialized, kind, key)) removed++;
            }

            var added = 0;
            if (action != BuildAction.Remove)
            {
                foreach (var key in frameKeys)
                {
                    if (frameExisting.Contains(key)) continue;
                    if (BindingFixer.AddKey(serialized, kind, key, typeIndex, required)) added++;
                }
            }

            if (added == 0 && removed == 0) return;

            Undo.SetCurrentGroupName(
                action == BuildAction.Add ? $"Add {added} {kind.DisplayName()} Bindings"
                : action == BuildAction.Remove ? $"Remove {removed} {kind.DisplayName()} Bindings"
                : $"Replace {kind.DisplayName()} Bindings");
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);

            // The open inspectors are showing a list that just grew under them.
            BindingReferenceDrawerContext.RaiseContextModified();

            // First is deliberately left where it was. Next pass every key is found already there,
            // so the button reads "Add 0" and the line beside it says so — which is what finished
            // looks like, without moving a number the person set.
        }
    }
}
