using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>
    /// Picking upload targets, in its own window rather than inline.
    ///
    /// An account can see forty buckets and a deploy uses three. Inline, that list either eats the
    /// main window or hides inside a nested scroll that fights the page scroll; neither is worth it
    /// for something touched once per campaign.
    /// </summary>
    public class BucketPickerWindow : EditorWindow
    {
        private EasyUploadSettings settings;
        // A live accessor rather than a captured list: the buckets arrive from a background
        // listing, so a snapshot taken when the picker opened would stay empty forever.
        private Func<List<string>> available;
        private string filter = "";
        private Vector2 scroll;
        private Action onChanged;
        private bool focusedFilter;

        private const float RowHeight = 22f;

        public static void Open(Rect anchor, EasyUploadSettings settings, Func<List<string>> available, Action onChanged)
        {
            var window = CreateInstance<BucketPickerWindow>();
            window.settings = settings;
            window.available = available ?? (() => new List<string>());
            window.onChanged = onChanged;
            window.titleContent = new GUIContent("Buckets");
            window.wantsMouseMove = true;   // otherwise row hover lags a tenth of a second behind

            var height = Mathf.Clamp(window.available().Count * RowHeight + 104f, 260f, 460f);
            window.ShowAsDropDown(anchor, new Vector2(Mathf.Max(340f, anchor.width), height));
        }

        private void OnInspectorUpdate()
        {
            // The listing lands on a background thread; without this the picker sits on "Loading…"
            // until the mouse moves.
            Repaint();
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint();
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);

            var matching = Matching();

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Upload targets", ToolStyles.CardTitle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(settings.buckets.Count + " selected", ToolStyles.StatusText);
                }

                GUILayout.Space(ToolStyles.SpaceS);

                GUI.SetNextControlName("bucket-filter");
                filter = EditorGUILayout.TextField(filter, EditorStyles.toolbarSearchField);
                if (!focusedFilter && Event.current.type == EventType.Repaint)
                {
                    // Opened by a click on a button, so nothing has focus yet — put the caret in the
                    // search box so the picker can be driven from the keyboard immediately.
                    EditorGUI.FocusTextInControl("bucket-filter");
                    focusedFilter = true;
                }

                GUILayout.Space(ToolStyles.SpaceS);

                scroll = EditorGUILayout.BeginScrollView(scroll);
                if (matching.Count == 0)
                {
                    GUILayout.Label(available().Count == 0
                        ? "Loading buckets…"
                        : "Nothing matches “" + filter + "”.", ToolStyles.Hint);
                }
                foreach (var bucket in matching) DrawRow(bucket);
                EditorGUILayout.EndScrollView();

                GUILayout.Space(ToolStyles.SpaceS);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new ToolStyles.DisabledScope(matching.Count == 0))
                    {
                        if (GUILayout.Button("Select all shown", ToolStyles.Secondary,
                            GUILayout.Height(ToolStyles.ControlHeight)))
                            SetAll(matching, true);
                        if (GUILayout.Button("Clear shown", ToolStyles.Secondary,
                            GUILayout.Height(ToolStyles.ControlHeight)))
                            SetAll(matching, false);
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Done", ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonM),
                        GUILayout.Height(ToolStyles.ActionHeight)))
                        Close();
                }
            }
        }

        private void DrawRow(string bucket)
        {
            var rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
            var selected = settings.buckets.Contains(bucket);
            var pinned = settings.IsFavorite(bucket);

            if (selected)
                EditorGUI.DrawRect(rect, ToolStyles.Blend(ToolStyles.Accent,
                    ToolStyles.CardBg, 0.82f));

            var starRect = new Rect(rect.x + 2, rect.y + 3, 16, 16);
            var star = new GUIContent(pinned ? "★" : "☆", pinned ? "Unpin" : "Pin to the top");
            var previous = GUI.contentColor;
            GUI.contentColor = pinned ? ToolStyles.Warn : ToolStyles.Faint;
            if (GUI.Button(starRect, star, EditorStyles.label)) settings.ToggleFavorite(bucket);
            GUI.contentColor = previous;
            EditorGUIUtility.AddCursorRect(starRect, MouseCursor.Link);

            var toggleRect = new Rect(rect.x + 22, rect.y + 3, rect.width - 24, 16);
            var now = EditorGUI.ToggleLeft(toggleRect, bucket, selected);
            if (now == selected) return;

            if (now) settings.buckets.Add(bucket);
            else settings.buckets.Remove(bucket);
            settings.Save();
            onChanged?.Invoke();
        }

        private void SetAll(List<string> buckets, bool value)
        {
            foreach (var bucket in buckets)
            {
                if (value && !settings.buckets.Contains(bucket)) settings.buckets.Add(bucket);
                else if (!value) settings.buckets.Remove(bucket);
            }
            settings.Save();
            onChanged?.Invoke();
        }

        /// <summary>Pinned first, then the rest in the order they came back, filtered by the search box.</summary>
        private List<string> Matching()
        {
            var pinned = new List<string>();
            var rest = new List<string>();
            var needle = (filter ?? "").Trim();

            foreach (var bucket in available())
            {
                if (needle.Length > 0 && bucket.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                (settings.IsFavorite(bucket) ? pinned : rest).Add(bucket);
            }

            pinned.Sort(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>(pinned);
            ordered.AddRange(rest);
            return ordered;
        }
    }
}
