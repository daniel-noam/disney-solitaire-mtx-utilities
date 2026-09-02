using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    [InitializeOnLoad]
    public static class TimescaleToolbarControl
    {
        private const float DefaultTimeScale = 1.0f;
        private const float MaxTimeScale = 5.0f;

        // Survives the domain reload that entering play mode triggers, which a plain static does not.
        // Session-scoped on purpose: a debug timescale should not still be 0.1x tomorrow morning.
        private const string SessionKey = "TimescaleToolbar_Target";

        private static float _targetTimeScale = DefaultTimeScale;

        static TimescaleToolbarControl()
        {
            _targetTimeScale = SessionState.GetFloat(SessionKey, DefaultTimeScale);

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            ToolbarExtender.RegisterRight("timescale", "Timescale Slider", DrawTimescaleSlider);
        }

        /// <summary>
        /// Applies the slider on entering play mode. Time.timeScale only means anything while playing, so
        /// a value dialled in beforehand used to be silently discarded.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) ApplyTimeScale();
        }

        private static void SetTarget(float value)
        {
            _targetTimeScale = value;
            SessionState.SetFloat(SessionKey, value);
            ApplyTimeScale();
        }

        private static void ApplyTimeScale()
        {
            if (EditorApplication.isPlaying) Time.timeScale = _targetTimeScale;
        }

        // Built once: the toolbar repaints continuously, so an inline GUIStyle was one allocation per frame.
        private static GUIStyle _labelStyle;

        private static GUIStyle LabelStyle => _labelStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft
        };

        private static void DrawTimescaleSlider()
        {
            GUILayout.Space(10);

            // Pick up an external change (game code touching Time.timeScale) before drawing, so the
            // readout matches the slider in the same frame rather than trailing it by one.
            if (EditorApplication.isPlaying && Mathf.Abs(Time.timeScale - _targetTimeScale) > 0.01f)
                _targetTimeScale = Time.timeScale;

            GUILayout.Label($"Timescale: {_targetTimeScale:F1}", LabelStyle, GUILayout.Width(85), GUILayout.Height(22));

            EditorGUI.BeginChangeCheck();
            float slider = GUILayout.HorizontalSlider(_targetTimeScale, 0f, MaxTimeScale, GUILayout.Width(90), GUILayout.Height(22));
            if (EditorGUI.EndChangeCheck()) SetTarget(slider);

            GUILayout.Space(4);

            using (new EditorGUI.DisabledScope(Mathf.Approximately(_targetTimeScale, DefaultTimeScale)))
            {
                if (GUILayout.Button("Reset", EditorStyles.miniButton, GUILayout.Width(45), GUILayout.Height(18)))
                    SetTarget(DefaultTimeScale);
            }
        }
    }
}
