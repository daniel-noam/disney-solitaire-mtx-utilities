using System;
using System.Collections.Generic;
using SuperPlay.Domino.TemplatesBehavior.Runtime;

namespace Tools.Editor.EditorUtilities
{
    public enum BindingListKind
    {
        SegmentData,
        LocalData,
        Object,
        Group,
        Asset
    }

    internal static class BindingListKindExtensions
    {
        /// <summary>
        /// Local Data rows use Unity's default drawer, so there is no custom row to hang an inline
        /// icon on - their issues have to go in the top summary instead. Single source of truth: the
        /// analyzer decides where to write an issue and the drawer context decides whether to read
        /// it, and the two must agree or issues silently vanish.
        /// </summary>
        public static bool SupportsInlineIssues(this BindingListKind kind) => kind != BindingListKind.LocalData;

        /// <summary>The serialized field the list lives in. One copy, because two would drift.</summary>
        public static string ArrayPath(this BindingListKind kind)
        {
            switch (kind)
            {
                case BindingListKind.SegmentData: return "_segmentData";
                case BindingListKind.LocalData: return "_localData";
                case BindingListKind.Object: return "_objects";
                case BindingListKind.Group: return "_groups";
                case BindingListKind.Asset: return "_assets";
                default: return string.Empty;
            }
        }

        /// <summary>The list's name as the inspector and the analyzer's messages write it.</summary>
        public static string DisplayName(this BindingListKind kind)
        {
            switch (kind)
            {
                case BindingListKind.SegmentData: return "Segment Data";
                case BindingListKind.LocalData: return "Local Data";
                default: return kind.ToString();
            }
        }
    }

    internal static class BindingReferenceDrawerContext
    {
        private static IReadOnlyDictionary<string, int> _segmentDataRefCounts;
        private static IReadOnlyDictionary<string, int> _localDataRefCounts;
        private static IReadOnlyDictionary<string, int> _objectRefCounts;
        private static IReadOnlyDictionary<string, int> _groupRefCounts;
        private static IReadOnlyDictionary<string, int> _assetRefCounts;
        private static IReadOnlyDictionary<BindingIssueKey, string> _inlineIssues;

        public static bool IsActive { get; private set; }
        public static TemplateBehavior CurrentScript { get; private set; }

        /// <summary>Raised when something outside the inspector edits bindings, so open editors can re-analyze.</summary>
        public static event Action OnContextModified;

        public static void RaiseContextModified() => OnContextModified?.Invoke();

        public static void Set(BindingReferenceAnalysis analysis, TemplateBehavior script)
        {
            if (analysis == null)
            {
                Clear();
                return;
            }

            _segmentDataRefCounts = analysis.SegmentDataRefCounts;
            _localDataRefCounts = analysis.LocalDataRefCounts;
            _objectRefCounts = analysis.ObjectRefCounts;
            _groupRefCounts = analysis.GroupRefCounts;
            _assetRefCounts = analysis.AssetRefCounts;
            _inlineIssues = analysis.InlineIssues;
            CurrentScript = script;
            IsActive = true;
        }

        public static void Clear()
        {
            _segmentDataRefCounts = null;
            _localDataRefCounts = null;
            _objectRefCounts = null;
            _groupRefCounts = null;
            _assetRefCounts = null;
            _inlineIssues = null;
            CurrentScript = null;
            IsActive = false;
        }

        public static bool TryGetRefCount(BindingListKind kind, string key, out int count)
        {
            count = 0;

            if (IsActive == false || string.IsNullOrEmpty(key))
                return false;

            var refCounts = kind switch
            {
                BindingListKind.SegmentData => _segmentDataRefCounts,
                BindingListKind.LocalData => _localDataRefCounts,
                BindingListKind.Object => _objectRefCounts,
                BindingListKind.Group => _groupRefCounts,
                BindingListKind.Asset => _assetRefCounts,
                _ => null
            };

            if (refCounts == null)
                return false;

            return refCounts.TryGetValue(key, out count);
        }

        public static bool TryGetInlineIssue(BindingListKind kind, string key, out string message)
        {
            message = null;

            if (IsActive == false || _inlineIssues == null || kind.SupportsInlineIssues() == false)
                return false;

            return _inlineIssues.TryGetValue(new BindingIssueKey(kind, key ?? string.Empty), out message);
        }
    }
}