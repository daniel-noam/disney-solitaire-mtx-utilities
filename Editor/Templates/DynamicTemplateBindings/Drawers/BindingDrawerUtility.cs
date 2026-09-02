using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    internal static class BindingDrawerUtility
    {
        public const float LineSpacing = 2f;
        private const float IssueIconWidth = 18f;

        /// <summary>How much of the palette's inset colour a striped row gets.</summary>
        private const float StripeAlpha = 0.6f;

        public static void DrawZebraBackground(Rect position, SerializedProperty property)
        {
            if (!DynamicTemplateBindingsSettings.Instance.stripeRows) return;

            int index = 0;
            string path = property.propertyPath;
            int openBracket = path.LastIndexOf('[');
            int closeBracket = path.LastIndexOf(']');
            
            if (openBracket != -1 && closeBracket != -1)
            {
                string indexStr = path.Substring(openBracket + 1, closeBracket - openBracket - 1);
                int.TryParse(indexStr, out index);
            }

            // Odd rows only: the inspector's own background is the other stripe. One rule for both
            // skins — the dark theme used to leave even rows clear while the light theme painted
            // them too, so the striping read at a different rhythm depending on the skin.
            if (index % 2 == 0) return;

            // Nothing in the inspector has built the palette: unlike the tool windows, a drawer has
            // no OnGUI of its own to call this from. Ensure returns immediately once it is built.
            ToolStyles.Ensure();

            var stripe = ToolStyles.InsetBg;
            stripe.a = StripeAlpha;

            Rect bgRect = new Rect(position.x - 4f, position.y - 2f, position.width + 8f, position.height + 4f);
            EditorGUI.DrawRect(bgRect, stripe);
        }

        public static float DrawNameWithRefCount(Rect rect, SerializedProperty nameProperty, BindingListKind kind)
        {
            var settings = DynamicTemplateBindingsSettings.Instance;

            var key = nameProperty.stringValue;
            var nameHeight = EditorGUI.GetPropertyHeight(nameProperty, true);
            var hasIssue = settings.showInlineIssues && HasInlineIssue(kind, key);
            var nameRect = new Rect(rect.x, rect.y, rect.width, nameHeight);

            // Declared up front: the short-circuit means the out parameter is not definitely
            // assigned when the setting is what fails.
            var count = 0;
            if (settings.showRefCounts && BindingReferenceDrawerContext.TryGetRefCount(kind, key, out count))
            {
                // If count code is -1, swap label string display format cleanly
                string refText = count >= 0 ? $"({count} refs)" : "(Dynamic)";
                var label = new GUIContent($"{nameProperty.displayName}  {refText}");
                EditorGUI.PropertyField(nameRect, nameProperty, label);
            }
            else
            {
                EditorGUI.PropertyField(nameRect, nameProperty);
            }

            if (hasIssue)
            {
                float iconX = rect.x + EditorGUIUtility.labelWidth - IssueIconWidth - 4f;
                float iconY = rect.y + (nameHeight - IssueIconWidth) * 0.5f;
                
                DrawIssueIcon(new Rect(iconX, iconY, IssueIconWidth, IssueIconWidth), kind, key);
            }

            return nameHeight;
        }

        public static float GetFieldsHeight(SerializedProperty property, params string[] relativePaths)
        {
            var height = 0f;
            for (var i = 0; i < relativePaths.Length; i++)
            {
                var field = property.FindPropertyRelative(relativePaths[i]);
                if (field == null) continue;

                if (i > 0) height += LineSpacing;
                height += EditorGUI.GetPropertyHeight(field, true);
            }
            return height;
        }

        private static bool HasInlineIssue(BindingListKind kind, string key) =>
            BindingReferenceDrawerContext.TryGetInlineIssue(kind, key, out _);

        private static void DrawIssueIcon(Rect rect, BindingListKind kind, string key)
        {
            if (BindingReferenceDrawerContext.TryGetInlineIssue(kind, key, out var message) == false)
                return;

            // Uses Contains so combined messages (e.g. an orphan that is also missing its value) still resolve
            // to the most severe icon, with errors taking precedence over info/warning tones.
            var isError = message.Contains("Duplicate key")
                || message.Contains("Key is empty")
                || message.Contains("Missing reference");
            var isInfo = isError == false && message.Contains("Potentially handled dynamically");
            
            string iconName = isError ? "console.erroricon.sml" : (isInfo ? "console.infoicon.sml" : "console.warnicon.sml");
            var icon = EditorGUIUtility.IconContent(iconName);

            var previousColor = GUI.color;
            if (isError) GUI.color = new Color(1f, 0.45f, 0.45f);
            else if (isInfo) GUI.color = new Color(0.35f, 0.65f, 1f); // soft blue info tone
            else GUI.color = new Color(1f, 0.75f, 0.3f);
            
            GUI.Label(rect, new GUIContent(icon.image, message));
            GUI.color = previousColor;
        }
    }
}