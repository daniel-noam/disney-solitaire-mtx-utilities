using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    [InitializeOnLoad]
    public static class BuildPlatformToolbarControl
    {
        // Candidate build targets shown in the dropdown (only the supported ones are listed).
        private static readonly BuildTarget[] CandidateTargets =
        {
            BuildTarget.StandaloneWindows64,
            BuildTarget.StandaloneOSX,
            BuildTarget.StandaloneLinux64,
            BuildTarget.Android,
            BuildTarget.iOS,
            BuildTarget.WebGL,
            BuildTarget.tvOS,
            BuildTarget.PS4,
            BuildTarget.PS5,
            BuildTarget.XboxOne,
            BuildTarget.Switch,
        };

        private static List<BuildTarget> _supportedTargets;
        private static string[] _supportedLabels;

        // Reflection cache for BuildPipeline.IsBuildTargetSupported (internal in most Unity versions).
        private static MethodInfo _isSupportedMethod;
        private static bool _resolvedSupportMethod;

        static BuildPlatformToolbarControl()
        {
            ToolbarExtender.RegisterRight("build_platform", "Build Platform", DrawPlatformDropdown);
        }

        // Built once: the toolbar repaints continuously, so an inline GUIStyle was one allocation per frame.
        private static GUIStyle _linkLabelStyle;

        private static GUIStyle LinkLabelStyle
        {
            get
            {
                if (_linkLabelStyle != null) return _linkLabelStyle;

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

        private static void DrawPlatformDropdown()
        {
            if (_supportedTargets == null)
            {
                RefreshSupportedTargets();
            }

            if (_supportedTargets == null || _supportedTargets.Count == 0)
            {
                return;
            }

            GUILayout.Space(10);

            // Clickable label opens the Build Settings window.
            if (GUILayout.Button("Build:", LinkLabelStyle, GUILayout.Width(38), GUILayout.Height(18)))
            {
                EditorApplication.ExecuteMenuItem("File/Build Settings...");
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
            int currentIndex = _supportedTargets.IndexOf(activeTarget);
            if (currentIndex < 0) currentIndex = 0;

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(currentIndex, _supportedLabels, GUILayout.Width(120));
            if (EditorGUI.EndChangeCheck() && newIndex != currentIndex)
            {
                BuildTarget selected = _supportedTargets[newIndex];
                BuildTarget current = _supportedTargets[currentIndex];

                bool confirmed = EditorUtility.DisplayDialog(
                    "Switch Build Platform",
                    $"Are you sure you want to switch the active build platform from {GetDisplayName(current)} to {GetDisplayName(selected)}?\n\nThis may take a while as Unity reimports assets for the new platform.",
                    "Switch",
                    "Cancel");

                if (!confirmed)
                {
                    return;
                }

                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(selected);

                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(group, selected);
                if (switched)
                {
                    Debug.Log($"[Toolbar] Switched active build platform to: <b>{GetDisplayName(selected)}</b>");
                }
                else
                {
                    Debug.LogWarning($"[Toolbar] Failed to switch build platform to: {GetDisplayName(selected)}");
                }
            }
        }

        private static void RefreshSupportedTargets()
        {
            _supportedTargets = new List<BuildTarget>();

            foreach (var target in CandidateTargets)
            {
                if (IsTargetSupported(target) && !_supportedTargets.Contains(target))
                {
                    _supportedTargets.Add(target);
                }
            }

            // Always make sure the currently active target is selectable, even if the module check fails.
            BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
            if (!_supportedTargets.Contains(active))
            {
                _supportedTargets.Insert(0, active);
            }

            _supportedLabels = _supportedTargets.Select(GetDisplayName).ToArray();
        }

        private static bool IsTargetSupported(BuildTarget target)
        {
            if (!_resolvedSupportMethod)
            {
                _resolvedSupportMethod = true;
                _isSupportedMethod = typeof(BuildPipeline).GetMethod(
                    "IsBuildTargetSupported",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(BuildTargetGroup), typeof(BuildTarget) },
                    null);
            }

            // If we can't resolve the internal check, be permissive and show the target.
            if (_isSupportedMethod == null)
            {
                return true;
            }

            try
            {
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                object result = _isSupportedMethod.Invoke(null, new object[] { group, target });
                return result is bool b && b;
            }
            catch
            {
                return true;
            }
        }

        private static string GetDisplayName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64: return "Windows";
                case BuildTarget.StandaloneOSX: return "macOS";
                case BuildTarget.StandaloneLinux64: return "Linux";
                case BuildTarget.Android: return "Android";
                case BuildTarget.iOS: return "iOS";
                case BuildTarget.WebGL: return "WebGL";
                case BuildTarget.tvOS: return "tvOS";
                case BuildTarget.PS4: return "PS4";
                case BuildTarget.PS5: return "PS5";
                case BuildTarget.XboxOne: return "Xbox One";
                case BuildTarget.Switch: return "Switch";
                default: return target.ToString();
            }
        }
    }
}
