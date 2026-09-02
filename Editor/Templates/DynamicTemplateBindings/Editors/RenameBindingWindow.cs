using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SuperPlay.Domino.TemplatesBehavior.Runtime;

namespace Tools.Editor.EditorUtilities
{
    internal class RenameBindingWindow : EditorWindow
    {
        private const string UndoGroupName = "Rename Binding Key";
        private const float WindowWidth = 390f;

        private BindingListKind _kind;
        private string _oldName;
        private string _newName;
        private SerializedObject _serializedObject;
        private string _propertyPath;
        private TemplateBehavior _graph;

        private int _companionCount;
        private string _oldPrefix;

        public static void Open(BindingListKind kind, string oldName, SerializedProperty property, TemplateBehavior graph)
        {
            var window = GetWindow<RenameBindingWindow>(true, "Rename Binding Key", true);
            window._kind = kind;
            window._oldName = oldName;
            window._newName = oldName;
            window._serializedObject = property.serializedObject;
            window._propertyPath = property.propertyPath;
            window._graph = graph;

            window._oldPrefix = GetTemplatePrefix(oldName);
            window._companionCount = window.CalculateCompanionCount();

            window.minSize = new Vector2(WindowWidth, 170f);
            window.maxSize = new Vector2(WindowWidth, 170f);
            window.ShowUtility();
        }

        private void OnGUI()
        {
            ToolStyles.Ensure();

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField($"Rename '{_oldName}' ({_kind}) everywhere:", EditorStyles.boldLabel);
            EditorGUILayout.Space(6f);

            _newName = EditorGUILayout.TextField("New Name", _newName);
            EditorGUILayout.Space(10f);

            string trimmedName = _newName == null ? string.Empty : _newName.Trim();
            bool isChanged = trimmedName.Length > 0 && trimmedName != _oldName;

            EditorGUILayout.HelpBox(
                "Notice: Values in other graphs that use this key will not be updated automatically (such as badge graphs).",
                MessageType.Info);
            EditorGUILayout.Space(6f);

            if (isChanged && NameAlreadyInUse(trimmedName))
            {
                EditorGUILayout.HelpBox(
                    $"'{trimmedName}' is already used by another entry in this list. Renaming will create a duplicate key.",
                    MessageType.Error);
                EditorGUILayout.Space(6f);
            }

            if (_companionCount > 0 && isChanged)
            {
                string newPrefix = GetTemplatePrefix(trimmedName);
                if (newPrefix != _oldPrefix)
                {
                    EditorGUILayout.HelpBox(
                        $"Batch Action Warning:\nChanging the prefix will automatically rename {_companionCount} other companion keys sharing the '{_oldPrefix}' prefix structure to use '{newPrefix}'.",
                        MessageType.Warning);
                    EditorGUILayout.Space(6f);
                }
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            // Confirm on the right, which is what a modal does, but weighted: Rename is the one that
            // writes to every graph that mentions the key, and it read as no more consequential
            // than the way out.
            if (GUILayout.Button("Cancel", ToolStyles.Secondary,
                    GUILayout.Width(ToolStyles.ButtonM), GUILayout.Height(ToolStyles.ActionHeight)))
            {
                Close();
            }

            using (new ToolStyles.DisabledScope(isChanged == false))
            {
                if (GUILayout.Button("Rename", ToolStyles.Primary,
                        GUILayout.Width(ToolStyles.ButtonM), GUILayout.Height(ToolStyles.ActionHeight)))
                {
                    ExecuteRename();
                    Close();
                }
            }

            EditorGUILayout.EndHorizontal();

            ResizeToContent();
        }

        /// <summary>
        /// The help boxes come and go as the name is typed, so the window is sized to whatever is
        /// actually laid out instead of to hardcoded heights that clip when both warnings show.
        /// </summary>
        private void ResizeToContent()
        {
            if (Event.current.type != EventType.Repaint) return;

            float contentHeight = GUILayoutUtility.GetLastRect().yMax + 10f;
            if (Mathf.Abs(contentHeight - minSize.y) < 1f) return;

            minSize = new Vector2(WindowWidth, contentHeight);
            maxSize = new Vector2(WindowWidth, contentHeight);
        }

        private void ExecuteRename()
        {
            if (_serializedObject == null) return;

            _newName = _newName.Trim();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            _serializedObject.Update();

            // Every key this rename touches, so the graph references can be updated in one pass.
            // Renaming the bindings without renaming all of the matching graph references is what
            // produces the "used in the graph but missing from bindings" errors this tool reports.
            var renames = new Dictionary<string, string>();

            var arrayProperty = FindOwningArrayProperty();
            if (arrayProperty != null)
            {
                string newPrefix = GetTemplatePrefix(_newName);

                if (!string.IsNullOrEmpty(_oldPrefix) && _oldPrefix != newPrefix)
                {
                    for (int i = 0; i < arrayProperty.arraySize; i++)
                    {
                        var elementNameProperty = arrayProperty.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                        if (elementNameProperty == null) continue;

                        string currentName = elementNameProperty.stringValue;
                        if (string.IsNullOrEmpty(currentName) || !currentName.StartsWith(_oldPrefix)) continue;

                        string renamedName = newPrefix + currentName.Substring(_oldPrefix.Length);
                        elementNameProperty.stringValue = renamedName;
                        renames[currentName] = renamedName;
                    }
                }
            }

            // The explicitly renamed entry wins over whatever the prefix pass gave it.
            renames[_oldName] = _newName;

            var property = _serializedObject.FindProperty(_propertyPath);
            if (property != null)
            {
                property.stringValue = _newName;
            }

            _serializedObject.ApplyModifiedProperties();

            if (_graph != null)
            {
                BindingGraphRefactorer.RenameReferences(_graph, _kind, renames);
            }

            Undo.SetCurrentGroupName(UndoGroupName);
            Undo.CollapseUndoOperations(undoGroup);

            BindingReferenceDrawerContext.RaiseContextModified();
        }

        private int CalculateCompanionCount()
        {
            if (string.IsNullOrEmpty(_oldPrefix)) return 0;

            var arrayProperty = FindOwningArrayProperty();
            if (arrayProperty == null) return 0;

            int count = 0;
            for (int i = 0; i < arrayProperty.arraySize; i++)
            {
                var elementNameProperty = arrayProperty.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (elementNameProperty == null) continue;

                string name = elementNameProperty.stringValue;
                if (name != _oldName && !string.IsNullOrEmpty(name) && name.StartsWith(_oldPrefix))
                {
                    count++;
                }
            }
            return count;
        }

        private bool NameAlreadyInUse(string candidate)
        {
            var arrayProperty = FindOwningArrayProperty();
            if (arrayProperty == null) return false;

            for (int i = 0; i < arrayProperty.arraySize; i++)
            {
                var elementNameProperty = arrayProperty.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (elementNameProperty != null && elementNameProperty.stringValue == candidate)
                    return true;
            }
            return false;
        }

        /// <summary>Resolves the binding list that owns the renamed element, e.g. "_assets" from "_assets.Array.data[3].name".</summary>
        private SerializedProperty FindOwningArrayProperty()
        {
            if (_serializedObject == null || string.IsNullOrEmpty(_propertyPath)) return null;

            int arrayTokenIndex = _propertyPath.LastIndexOf(".Array", System.StringComparison.Ordinal);
            if (arrayTokenIndex == -1) return null;

            var arrayProperty = _serializedObject.FindProperty(_propertyPath.Substring(0, arrayTokenIndex));
            return arrayProperty != null && arrayProperty.isArray ? arrayProperty : null;
        }

        private static string GetTemplatePrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int i = name.Length - 1;
            while (i >= 0 && char.IsDigit(name[i]))
            {
                i--;
            }
            return name.Substring(0, i + 1);
        }
    }
}
