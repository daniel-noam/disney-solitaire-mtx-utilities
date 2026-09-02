using System;
using System.Reflection;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Utilities.Editor
{
    /// <summary>
    /// Applies a <see cref="BindingFix"/> to the bindings component.
    ///
    /// Written through SerializedProperty rather than the component's own fields so the change goes
    /// through the inspector's undo and prefab-override machinery like any hand edit would - the
    /// caller only has to ApplyModifiedProperties afterwards.
    /// </summary>
    internal static class BindingFixer
    {
        public static bool Apply(SerializedObject serializedObject, BindingFix fix)
        {
            if (serializedObject == null || fix == null) return false;

            var list = serializedObject.FindProperty(fix.Kind.ArrayPath());
            if (list == null || !list.isArray) return false;

            return fix.Action == BindingFixKind.AddMissingKey
                ? AddKey(list, fix.Key, 0, false)
                : RemoveKey(list, fix.Key);
        }

        /// <summary>
        /// Appends one binding. <paramref name="typeIndex"/> and <paramref name="required"/> are
        /// ignored by the lists that have no such field.
        /// </summary>
        public static bool AddKey(SerializedObject serializedObject, BindingListKind kind, string key,
            int typeIndex, bool required)
        {
            if (serializedObject == null || string.IsNullOrEmpty(key)) return false;

            var list = serializedObject.FindProperty(kind.ArrayPath());
            return list != null && list.isArray && AddKey(list, key, typeIndex, required);
        }

        /// <summary>Deletes the binding with this key, if the list has one.</summary>
        public static bool RemoveKey(SerializedObject serializedObject, BindingListKind kind, string key)
        {
            if (serializedObject == null || string.IsNullOrEmpty(key)) return false;

            var list = serializedObject.FindProperty(kind.ArrayPath());
            return list != null && list.isArray && RemoveKey(list, key);
        }

        /// <summary>
        /// The names of a list's Type field, empty for the lists that have no such field.
        ///
        /// Read off the binding class rather than off an existing entry: a template with an empty
        /// list is exactly the one you are most likely to be filling in, and there would be no
        /// entry to read from.
        /// </summary>
        public static string[] TypeNames(BindingListKind kind)
        {
            var listField = typeof(DynamicTemplateBindings).GetField(kind.ArrayPath(),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var element = ElementType(listField?.FieldType);
            var typeField = element?.GetField("type",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return typeField != null && typeField.FieldType.IsEnum
                ? Enum.GetNames(typeField.FieldType)
                : Array.Empty<string>();
        }

        private static Type ElementType(Type listType)
        {
            if (listType == null) return null;
            if (listType.IsArray) return listType.GetElementType();
            return listType.IsGenericType ? listType.GetGenericArguments()[0] : null;
        }

        /// <summary>A description of the change, for the undo entry it becomes.</summary>
        public static string Describe(BindingFix fix) =>
            fix == null
                ? string.Empty
                : fix.Action == BindingFixKind.AddMissingKey
                    ? $"Add {fix.Category} Binding \"{fix.Key}\""
                    : $"Remove {fix.Category} Binding \"{fix.Key}\"";

        private static bool AddKey(SerializedProperty list, string key, int typeIndex, bool required)
        {
            var index = list.arraySize;
            list.InsertArrayElementAtIndex(index);

            var element = list.GetArrayElementAtIndex(index);
            if (element == null) return false;

            // Inserting past the end copies the last entry, so every field the new one has is set
            // explicitly. Otherwise the new binding arrives carrying the previous one's value.
            SetString(element, "name", key);
            ClearValue(element, "value");
            SetEnum(element, "type", typeIndex);
            SetBool(element, "isRequired", required);
            return true;
        }

        private static bool RemoveKey(SerializedProperty list, string key)
        {
            for (var i = 0; i < list.arraySize; i++)
            {
                var name = list.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (name == null || name.stringValue != key) continue;

                list.DeleteArrayElementAtIndex(i);
                return true;
            }

            return false;
        }

        private static void SetString(SerializedProperty element, string relative, string value)
        {
            var property = element.FindPropertyRelative(relative);
            if (property != null && property.propertyType == SerializedPropertyType.String)
                property.stringValue = value;
        }

        private static void SetBool(SerializedProperty element, string relative, bool value)
        {
            var property = element.FindPropertyRelative(relative);
            if (property != null && property.propertyType == SerializedPropertyType.Boolean)
                property.boolValue = value;
        }

        private static void SetEnum(SerializedProperty element, string relative, int value)
        {
            var property = element.FindPropertyRelative(relative);
            if (property != null && property.propertyType == SerializedPropertyType.Enum)
                property.enumValueIndex = value;
        }

        private static void ClearValue(SerializedProperty element, string relative)
        {
            var property = element.FindPropertyRelative(relative);
            if (property == null) return;

            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = null;
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = string.Empty;
                    break;
            }
        }
    }
}
