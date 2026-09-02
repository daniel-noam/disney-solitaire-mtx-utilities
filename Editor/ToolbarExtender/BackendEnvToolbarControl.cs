using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    [InitializeOnLoad]
    public static class BackendEnvToolbarControl
    {
        private static ScriptableObject _cachedConfigAsset = null;
        private static Type _cachedConfigType = null;
        private static Type _cachedEnvEnumType = null;
        private static FieldInfo _cachedEnvField = null;

        // Statics reset on domain reload, so a recompile naturally retries the lookup.
        private static bool _resolveAttempted;

        static BackendEnvToolbarControl()
        {
            ToolbarExtender.RegisterRight("backend_env", "Backend Environment", DrawEnvironmentDropdown);
        }

        // Built once: the toolbar repaints continuously, so an inline GUIStyle was one allocation per frame.
        private static GUIStyle _linkLabelStyle;

        private static GUIStyle LinkLabelStyle
        {
            get
            {
                if (_linkLabelStyle != null) return _linkLabelStyle;

                // Inherits the default light/dark skin colour naturally; only the interactive states are
                // tinted blue to signal that the label is clickable.
                _linkLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = FontStyle.Bold
                };
                _linkLabelStyle.hover.textColor = new Color(0.35f, 0.65f, 1f);
                _linkLabelStyle.active.textColor = new Color(0.2f, 0.5f, 0.8f);

                return _linkLabelStyle;
            }
        }

        private static void DrawEnvironmentDropdown()
        {
            // Resolution is attempted once per domain rather than on every repaint. Previously, a project
            // without these types re-scanned every loaded assembly twice AND ran a project-wide
            // FindAssets("BackendConfiguration") every single frame, forever.
            if (!_resolveAttempted)
            {
                _resolveAttempted = true;

                _cachedConfigType = FindTypeEverywhere("SuperPlay.Domino.DataSource.BackendConfiguration");
                _cachedEnvEnumType = FindTypeEverywhere("SuperPlay.Domino.Data.BackendEnvironment");

                if (_cachedConfigType != null)
                    _cachedEnvField = _cachedConfigType.GetField("environment", BindingFlags.Public | BindingFlags.Instance);

                if (_cachedConfigType != null && _cachedEnvEnumType != null && _cachedEnvField != null)
                    _cachedConfigAsset = FindBackendConfigurationAssetReflection();
            }

            // Hide the toolbar element entirely when the types or the asset are missing.
            if (_cachedEnvEnumType == null || _cachedEnvField == null || _cachedConfigAsset == null)
                return;

            GUILayout.Space(10);

            // Clickable Label Engine
            if (GUILayout.Button("Env:", LinkLabelStyle, GUILayout.Width(28), GUILayout.Height(18)))
            {
                Selection.activeObject = _cachedConfigAsset;
                EditorGUIUtility.PingObject(_cachedConfigAsset);
            }

            // Hover Effect: Change mouse cursor to a pointing hand
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            // Dropdown execution loop
            object currentEnumValue = _cachedEnvField.GetValue(_cachedConfigAsset);
            string[] enumNames = Enum.GetNames(_cachedEnvEnumType);
            int currentIndex = Array.IndexOf(enumNames, currentEnumValue.ToString());

            if (currentIndex < 0) currentIndex = 0;

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(currentIndex, enumNames, GUILayout.Width(95));
            
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_cachedConfigAsset, "Modify Backend Environment Target");
                
                object newEnumValue = Enum.Parse(_cachedEnvEnumType, enumNames[newIndex]);
                _cachedEnvField.SetValue(_cachedConfigAsset, newEnumValue);
                
                EditorUtility.SetDirty(_cachedConfigAsset);

                // Only this asset - a blanket SaveAssets() also commits unrelated dirty assets.
                AssetDatabase.SaveAssetIfDirty(_cachedConfigAsset);


                Debug.Log($"[Toolbar] Switched Backend Environment to: <b>{enumNames[newIndex]}</b>");
            }
        }

        private static ScriptableObject FindBackendConfigurationAssetReflection()
        {
            string[] assetGuids = AssetDatabase.FindAssets("BackendConfiguration");
            if (assetGuids != null)
            {
                foreach (string guid in assetGuids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (assetPath.EndsWith(".asset"))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                        if (asset != null && asset.GetType() == _cachedConfigType)
                        {
                            return asset;
                        }
                    }
                }
            }
            return null;
        }

        private static Type FindTypeEverywhere(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = assembly.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}