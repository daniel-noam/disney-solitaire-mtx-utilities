using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Utilities.Editor
{
    /// <summary>
    /// Injects IMGUI containers into Unity's built-in toolbar by reflecting into UnityEditor.Toolbar,
    /// which has no public extension point. Everything here is version-sensitive by nature, so each
    /// lookup that fails says so once instead of failing silently or throwing every frame.
    /// </summary>
    [InitializeOnLoad]
    public static class ToolbarCallback
    {
        private const string ToolbarTypeName = "UnityEditor.Toolbar";
        private const string RootFieldName = "m_Root";
        private const string LeftZoneName = "ToolbarZoneLeftAlign";
        private const string RightZoneName = "ToolbarZoneRightAlign";

        private static ScriptableObject _currentToolbar;
        private static bool _warnedMissingRoot;

        public static Action OnToolbarGUILeft;
        public static Action OnToolbarGUIRight;

        static ToolbarCallback()
        {
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            // The toolbar is recreated on layout changes, so a destroyed reference means re-attach.
            if (_currentToolbar != null) return;

            Type toolbarType = typeof(UnityEditor.Editor).Assembly.GetType(ToolbarTypeName);
            if (toolbarType == null)
            {
                // Previously this fell through to FindObjectsOfTypeAll(null), which throws - once per
                // editor tick, forever. Stop polling instead.
                Debug.LogWarning($"[Toolbar] '{ToolbarTypeName}' not found, so toolbar extensions are " +
                                 "disabled. This Unity version has likely moved or renamed the type.");
                EditorApplication.update -= OnUpdate;
                return;
            }

            var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
            if (toolbars.Length == 0) return;

            _currentToolbar = (ScriptableObject)toolbars[0];

            var rootField = _currentToolbar.GetType().GetField(RootFieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (!(rootField?.GetValue(_currentToolbar) is VisualElement root))
            {
                // Warn once: _currentToolbar is now set, so this path is not retried.
                if (!_warnedMissingRoot)
                {
                    _warnedMissingRoot = true;
                    Debug.LogWarning($"[Toolbar] Could not read '{RootFieldName}' from the toolbar, so " +
                                     "toolbar extensions are disabled.");
                }
                return;
            }

            RegisterCallback(root, LeftZoneName, () => OnToolbarGUILeft?.Invoke());
            RegisterCallback(root, RightZoneName, () => OnToolbarGUIRight?.Invoke());
        }

        private static void RegisterCallback(VisualElement root, string zoneName, Action onGUI)
        {
            VisualElement toolbarZone = root.Q(zoneName);
            if (toolbarZone == null) return;

            var container = new IMGUIContainer();
            container.style.flexGrow = 0;
            container.style.flexShrink = 0;
            container.onGUIHandler += () => onGUI?.Invoke();

            var spacer = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexShrink = 0
                }
            };

            // Appending the growing spacer before the container pushes the container to the far end of its
            // zone. For the left zone that is the inner edge, next to the centre Play controls; for the
            // right zone it is the outer edge, at the far right of the window.
            //
            // This used to be an if/else on the zone, but both branches did exactly this - the "right"
            // branch's comment claimed it moved the container towards the Play controls and never did.
            // To actually place the right zone's items beside the Play controls, add the container before
            // the spacer for that zone.
            toolbarZone.Add(spacer);
            toolbarZone.Add(container);
        }
    }
}
