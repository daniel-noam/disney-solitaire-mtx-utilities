using System.Collections.Generic;
using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Reads the binding lists off a <see cref="DynamicTemplateBindings"/> component through one
    /// shared <see cref="SerializedObject"/>. Analysis reads eight lists per pass, so this is created
    /// once per pass rather than per list.
    /// </summary>
    internal sealed class DynamicTemplateBindingsSnapshot
    {
        private const string SegmentDataProperty = "_segmentData";
        private const string LocalDataProperty = "_localData";
        private const string ObjectsProperty = "_objects";
        private const string GroupsProperty = "_groups";
        private const string AssetsProperty = "_assets";

        private readonly SerializedObject _serializedObject;

        public DynamicTemplateBindingsSnapshot(DynamicTemplateBindings bindings)
        {
            _serializedObject = new SerializedObject(bindings);
        }

        public IReadOnlyList<string> GetSegmentDataNames() => GetBindingNames(SegmentDataProperty);

        public IReadOnlyList<string> GetLocalDataNames() => GetBindingNames(LocalDataProperty);

        public IReadOnlyList<string> GetObjectNames() => GetBindingNames(ObjectsProperty);

        public IReadOnlyList<string> GetGroupNames() => GetBindingNames(GroupsProperty);

        public IReadOnlyList<string> GetAssetNames() => GetBindingNames(AssetsProperty);

        public IReadOnlyList<MissingReferenceEntry> GetObjectMissingReferences() => GetMissingReferences(ObjectsProperty);

        public IReadOnlyList<MissingReferenceEntry> GetGroupMissingReferences() => GetMissingReferences(GroupsProperty);

        public IReadOnlyList<MissingReferenceEntry> GetAssetMissingReferences() => GetMissingReferences(AssetsProperty);

        private IReadOnlyList<string> GetBindingNames(string propertyName)
        {
            var names = new List<string>();
            var property = _serializedObject.FindProperty(propertyName);

            if (property == null || property.isArray == false)
                return names;

            for (var i = 0; i < property.arraySize; i++)
            {
                var nameProperty = property.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (nameProperty != null)
                    names.Add(nameProperty.stringValue);
            }

            return names;
        }

        private IReadOnlyList<MissingReferenceEntry> GetMissingReferences(string propertyName)
        {
            var entries = new List<MissingReferenceEntry>();
            var property = _serializedObject.FindProperty(propertyName);

            if (property == null || property.isArray == false)
                return entries;

            for (var i = 0; i < property.arraySize; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                var valueProperty = element.FindPropertyRelative("value");

                var kind = Classify(valueProperty);
                if (kind == ReferenceState.Fine)
                    continue;

                var nameProperty = element.FindPropertyRelative("name");
                var name = nameProperty != null ? nameProperty.stringValue : string.Empty;
                entries.Add(new MissingReferenceEntry(name, i, kind));
            }

            return entries;
        }

        /// <summary>
        /// Two different faults, which look the same to a null check and are not the same thing to
        /// fix. An empty field was never filled in; a broken one was, and what it pointed at has
        /// since been deleted - the state Unity draws as "Missing (GameObject)".
        ///
        /// They are told apart by the instance id, which survives the deletion of the object it
        /// refers to. Testing the id against zero alone, as this used to, reads a broken reference
        /// as a healthy one and says nothing about it at all.
        /// </summary>
        private static ReferenceState Classify(SerializedProperty valueProperty)
        {
            if (valueProperty == null)
                return ReferenceState.Fine;

            // GroupBinding.value is a GameObject[]: at fault when empty, or when any element is.
            // The worst element wins, because a deleted object is the more specific complaint.
            if (valueProperty.isArray && valueProperty.propertyType != SerializedPropertyType.String)
            {
                if (valueProperty.arraySize == 0)
                    return ReferenceState.Unassigned;

                var worst = ReferenceState.Fine;
                for (var i = 0; i < valueProperty.arraySize; i++)
                {
                    var state = ClassifyObjectReference(valueProperty.GetArrayElementAtIndex(i));
                    if (state == ReferenceState.Broken) return ReferenceState.Broken;
                    if (state == ReferenceState.Unassigned) worst = ReferenceState.Unassigned;
                }

                return worst;
            }

            // ObjectBinding.value (GameObject) and AssetBinding.value (LazyLoadReference<Object>)
            // both surface as an object reference.
            if (valueProperty.propertyType == SerializedPropertyType.ObjectReference)
                return ClassifyObjectReference(valueProperty);

            // Fallback for LazyLoadReference when Unity exposes it as a struct rather than an object
            // reference. Resolving the id is the only way in from here, so a broken reference is
            // reported best-effort: an id that no longer names anything.
            var instanceIdProperty = valueProperty.FindPropertyRelative("m_InstanceID");
            if (instanceIdProperty == null)
                return ReferenceState.Fine;

            if (instanceIdProperty.intValue == 0)
                return ReferenceState.Unassigned;

            return EditorUtility.InstanceIDToObject(instanceIdProperty.intValue) == null
                ? ReferenceState.Broken
                : ReferenceState.Fine;
        }

        private static ReferenceState ClassifyObjectReference(SerializedProperty property)
        {
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
                return ReferenceState.Fine;

            if (property.objectReferenceInstanceIDValue == 0)
                return ReferenceState.Unassigned;

            // An id that still resolves to nothing: the field holds a reference to something that
            // has been deleted.
            return property.objectReferenceValue == null
                ? ReferenceState.Broken
                : ReferenceState.Fine;
        }
    }

    internal enum ReferenceState
    {
        Fine,
        /// <summary>Nothing was ever put in the field.</summary>
        Unassigned,
        /// <summary>Something was, and it has since been deleted.</summary>
        Broken,
    }

    internal readonly struct MissingReferenceEntry
    {
        public string Name { get; }
        public int Index { get; }
        public ReferenceState State { get; }

        public MissingReferenceEntry(string name, int index, ReferenceState state)
        {
            Name = name ?? string.Empty;
            Index = index;
            State = state;
        }
    }
}
