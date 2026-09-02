using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    public class CheatEditorWindow : EditorWindow
    {
        private string _srOptionsName = "";
        private string _displayLabel = "";
        private SRDebuggerToolbarControl.CheatType _type;
        private float _min = 0f;
        private float _max = 1f;
        
        private bool _isEditing = false;
        private string _originalName = "";

        public static void OpenForAdd()
        {
            var window = GetWindow<CheatEditorWindow>(true, "Add Custom Cheat", true);
            window._isEditing = false;
            window._srOptionsName = "";
            window._displayLabel = "";
            SetWindowSize(window);
            window.ShowUtility();
        }

        public static void OpenForEdit(string name, string label, SRDebuggerToolbarControl.CheatType type, float min, float max)
        {
            var window = GetWindow<CheatEditorWindow>(true, "Edit Custom Cheat", true);
            window._isEditing = true;
            window._originalName = name;
            window._srOptionsName = name;
            window._displayLabel = label;
            window._type = type;
            window._min = min;
            window._max = max;
            SetWindowSize(window);
            window.ShowUtility();
        }

        private static void SetWindowSize(CheatEditorWindow window)
        {
            window.minSize = new Vector2(360, 150);
            window.maxSize = new Vector2(360, 150);
        }

        void OnGUI()
        {
            GUILayout.Space(12);

            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Please enter Play Mode to view and select options directly from your live SRDebugger options screen!", MessageType.Info);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Height(24))) Close();
                GUILayout.Space(10);
                return;
            }

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Select Cheat:", GUILayout.Width(100));
            
            string buttonText = string.IsNullOrEmpty(_srOptionsName) ? "Select from live SRDebugger screen" : $"{_srOptionsName} ({_type}) ▼";
            
            if (GUILayout.Button(buttonText, EditorStyles.popup))
            {
                ShowOptionsDropdown(GUILayoutUtility.GetLastRect());
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            _displayLabel = EditorGUILayout.TextField("Display Label:", _displayLabel);

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(24)))
            {
                Close();
            }
            
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_srOptionsName)))
            {
                if (GUILayout.Button(_isEditing ? "Save Changes" : "Add to Toolbar", GUILayout.Height(24)))
                {
                    if (_isEditing)
                    {
                        SRDebuggerToolbarControl.UnregisterCheat(_originalName);
                    }

                    SRDebuggerToolbarControl.RegisterCheat(_srOptionsName, _displayLabel, _type, _min, _max);
                    Close();
                }
            }
            
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }

        private void ShowOptionsDropdown(Rect buttonRect)
        {
            GenericMenu menu = new GenericMenu();
            var liveOptions = SRDebuggerToolbarControl.GetLiveSRDebuggerOptions();

            if (liveOptions == null || liveOptions.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("(No active options found on your SRDebugger screen)"));
                menu.DropDown(buttonRect);
                return;
            }

            var sortedOptions = liveOptions.OrderBy(opt => opt.Category).ThenBy(opt => opt.Name).ToList();

            foreach (var opt in sortedOptions)
            {
                string typeCategoryName = "";
                switch (opt.Type)
                {
                    case SRDebuggerToolbarControl.CheatType.MethodButton: typeCategoryName = "Buttons (Actions)"; break;
                    case SRDebuggerToolbarControl.CheatType.PropertyToggle: typeCategoryName = "Toggles (Checkboxes)"; break;
                    case SRDebuggerToolbarControl.CheatType.NumericSlider: typeCategoryName = "Sliders (Numbers)"; break;
                    case SRDebuggerToolbarControl.CheatType.StringTextField: typeCategoryName = "Text Fields (Strings)"; break;
                }

                string path = $"{opt.Category}/{typeCategoryName}/{opt.Name}";
                
                menu.AddItem(new GUIContent(path), _srOptionsName == opt.Name, () => {
                    _srOptionsName = opt.Name;
                    _type = opt.Type;
                    _min = opt.Min;
                    _max = opt.Max;
                    _displayLabel = Regex.Replace(opt.Name, "([a-z])([A-Z])", "$1 $2");
                });
            }

            menu.DropDown(buttonRect);
        }
    }
}