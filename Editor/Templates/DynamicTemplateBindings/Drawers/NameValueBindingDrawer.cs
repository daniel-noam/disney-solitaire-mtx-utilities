using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Shared drawer for the binding types that are just a name plus a single value
    /// (Object, Group, Asset). Subclasses only declare which binding list they belong to.
    /// </summary>
    public abstract class NameValueBindingDrawer : PropertyDrawer
    {
        private const float RowPadding = 4f;

        protected abstract BindingListKind Kind { get; }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return BindingDrawerUtility.GetFieldsHeight(property, "name", "value") + RowPadding;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            BindingDrawerUtility.DrawZebraBackground(position, property);

            var nameProperty = property.FindPropertyRelative("name");
            var valueProperty = property.FindPropertyRelative("value");

            var y = position.y + RowPadding * 0.5f;
            var nameHeight = BindingDrawerUtility.DrawNameWithRefCount(
                new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                nameProperty,
                Kind);
            y += nameHeight + BindingDrawerUtility.LineSpacing;

            var valueHeight = EditorGUI.GetPropertyHeight(valueProperty, true);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, valueHeight), valueProperty, true);

            EditorGUI.EndProperty();
        }
    }
}
