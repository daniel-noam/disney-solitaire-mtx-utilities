using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    [CustomPropertyDrawer(typeof(SegmentDataBinding))]
    public class SegmentDataBindingDrawer : PropertyDrawer
    {
        private const float RowPadding = 4f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return BindingDrawerUtility.GetFieldsHeight(property, "name", "type", "isRequired") + RowPadding;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            BindingDrawerUtility.DrawZebraBackground(position, property);

            var nameProperty = property.FindPropertyRelative("name");
            var typeProperty = property.FindPropertyRelative("type");
            var requiredProperty = property.FindPropertyRelative("isRequired");

            var y = position.y + RowPadding * 0.5f;

            var nameHeight = BindingDrawerUtility.DrawNameWithRefCount(
                new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                nameProperty,
                BindingListKind.SegmentData);
            y += nameHeight + BindingDrawerUtility.LineSpacing;

            y += DrawField(position, y, typeProperty) + BindingDrawerUtility.LineSpacing;
            DrawField(position, y, requiredProperty);

            EditorGUI.EndProperty();
        }

        private static float DrawField(Rect position, float y, SerializedProperty property)
        {
            var height = EditorGUI.GetPropertyHeight(property, true);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, height), property, true);
            return height;
        }
    }
}
