using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Switches for what the bindings inspector adds to a DynamicTemplateBindings component.
    ///
    /// A window rather than a Preferences page so it sits with the rest of the toolset under
    /// Utilities/, and so it can say what each switch actually costs.
    /// </summary>
    public class DynamicTemplateBindingsSettingsWindow : EditorWindow
    {
        private static readonly Vector2 MinWindowSize = new Vector2(400, 240);

        private DynamicTemplateBindingsSettings settings;

        [MenuItem("Utilities/Dynamic Template Bindings Settings", false, 1009)]
        public static void ShowWindow()
        {
            GetWindow<DynamicTemplateBindingsSettingsWindow>("Dynamic Template Bindings Settings")
                .minSize = MinWindowSize;
        }

        private void OnEnable()
        {
            settings = DynamicTemplateBindingsSettings.Instance;

            // Without this, hover states only repaint when something else happens to trigger a frame.
            wantsMouseMove = true;
        }

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            if (settings == null) settings = DynamicTemplateBindingsSettings.Instance;

            EditorGUI.BeginChangeCheck();

            // One margin of backdrop around everything, so the panels read as panels rather than as
            // slabs pushed up against the window frame.
            GUILayout.Space(ToolStyles.SpaceL);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawAnalysisCard();
                    GUILayout.Space(ToolStyles.SpaceM);
                    DrawInspectorCard();
                    GUILayout.Space(ToolStyles.SpaceM);
                    DrawFooter();
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }
            GUILayout.Space(ToolStyles.SpaceL);

            if (!EditorGUI.EndChangeCheck()) return;

            settings.Save();

            // The open inspectors re-analyse and repaint. Switching the analysis back on has to
            // rebuild what was skipped while it was off, so this is a stale mark, not a repaint.
            BindingReferenceDrawerContext.RaiseContextModified();
        }

        private void DrawAnalysisCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Graph analysis");

                settings.showSummary = Toggle("Issue summary", settings.showSummary,
                    "The box at the top listing every key that is missing, unused or duplicated.");

                settings.showRefCounts = Toggle("Reference counts", settings.showRefCounts,
                    "The '(3 refs)' after a key's label, saying how many graph nodes use it.");

                settings.showInlineIssues = Toggle("Issue icons", settings.showInlineIssues,
                    "The warning icon beside a key that has something wrong with it.");
            }
        }

        private void DrawInspectorCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Inspector");

                settings.stripeRows = Toggle("Striped rows", settings.stripeRows,
                    "Shade every other row, so a key and its value read as one entry.");

                settings.renameMenuItem = Toggle("Rename in the right-click menu", settings.renameMenuItem,
                    "The 'Rename key and graph references' entry on a key's right-click menu. It " +
                    "rewrites every node in the graph that mentions the key, so it works even " +
                    "with everything above turned off.");
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Reset", "Turn everything back on."),
                        ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                        GUILayout.Height(ToolStyles.ActionHeight)))
                    settings.ResetToDefaults();

                GUILayout.FlexibleSpace();
            }
        }

        private static bool Toggle(string label, bool value, string tooltip) =>
            EditorGUILayout.ToggleLeft(new GUIContent(label, tooltip), value,
                GUILayout.Height(ToolStyles.ControlHeight));
    }
}
