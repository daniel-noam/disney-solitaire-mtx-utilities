using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    [InitializeOnLoad]
    public static class ToolbarExtender
    {
        public enum ToolbarSide { Left, Right }

        public class ToolbarItem
        {
            public string Id;
            public string Label;
            public Action Draw;
            public ToolbarSide DefaultSide;
        }

        // Legacy anonymous handlers (kept for backwards compatibility, always drawn, not movable).
        public static readonly List<Action> LeftToolbarGUI = new List<Action>();
        public static readonly List<Action> RightToolbarGUI = new List<Action>();

        // Master registry of all named items (registration order preserved).
        private static readonly List<ToolbarItem> _items = new List<ToolbarItem>();

        // Id lookup for the draw loop, which previously ran a LINQ FirstOrDefault per item per repaint.
        private static readonly Dictionary<string, ToolbarItem> _itemsById = new Dictionary<string, ToolbarItem>();

        // Items whose Draw threw. Session-only, so a fix plus a recompile brings them back.
        private static readonly HashSet<string> _failedItems = new HashSet<string>();

        // Set by Register so the layout is reconciled when the registry changes, not on every repaint.
        private static bool _layoutDirty = true;

        [Serializable]
        private class LayoutData
        {
            public List<string> left = new List<string>();   // ordered item ids on the left side
            public List<string> right = new List<string>();  // ordered item ids on the right side
            public List<string> hidden = new List<string>(); // item ids that are toggled off
        }

        private static LayoutData _data;

        // Per-user JSON file living under <project>/UserSettings, which a standard Unity .gitignore
        // already covers - declared below anyway, for projects whose .gitignore does not.
        private const string ProjectRelativeLayoutPath = "UserSettings/ToolbarExtenderLayout.json";

        private static string LayoutFilePath
        {
            get
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(projectRoot, ProjectRelativeLayoutPath);
            }
        }

        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            yield return ProjectRelativeLayoutPath;
        }

        static ToolbarExtender()
        {
            ToolbarCallback.OnToolbarGUILeft = DrawLeft;
            ToolbarCallback.OnToolbarGUIRight = DrawRight;
        }

        #region Registration API

        public static void Register(ToolbarSide side, string id, string label, Action draw)
        {
            _items.RemoveAll(i => i.Id == id);

            var item = new ToolbarItem { Id = id, Label = label, Draw = draw, DefaultSide = side };
            _items.Add(item);
            _itemsById[id] = item;

            // Re-registering clears a previous failure so a recompiled item gets another chance.
            _failedItems.Remove(id);
            _layoutDirty = true;
        }

        public static void RegisterLeft(string id, string label, Action draw) => Register(ToolbarSide.Left, id, label, draw);
        public static void RegisterRight(string id, string label, Action draw) => Register(ToolbarSide.Right, id, label, draw);

        public static bool IsEnabled(string id)
        {
            EnsureLoaded();
            return !_data.hidden.Contains(id);
        }

        public static void SetEnabled(string id, bool enabled)
        {
            EnsureLoaded();
            if (enabled) _data.hidden.Remove(id);
            else if (!_data.hidden.Contains(id)) _data.hidden.Add(id);
            Save();
        }

        #endregion

        #region Layout persistence (JSON in UserSettings/)

        private static void EnsureLoaded()
        {
            if (_data != null) return;
            _data = Load();
        }

        private static LayoutData Load()
        {
            try
            {
                if (File.Exists(LayoutFilePath))
                {
                    string json = File.ReadAllText(LayoutFilePath);
                    var parsed = JsonUtility.FromJson<LayoutData>(json);
                    if (parsed != null)
                    {
                        if (parsed.left == null) parsed.left = new List<string>();
                        if (parsed.right == null) parsed.right = new List<string>();
                        if (parsed.hidden == null) parsed.hidden = new List<string>();
                        return parsed;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Toolbar] Failed to load layout, using defaults: {e.Message}");
            }
            return new LayoutData();
        }

        private static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(LayoutFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(LayoutFilePath, JsonUtility.ToJson(_data, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Toolbar] Failed to save layout: {e.Message}");
            }
        }

        // Ensure every registered item appears in exactly one side's order list.
        private static void SyncLayout()
        {
            EnsureLoaded();

            // Only meaningful after a registration change. This runs from the draw path, where it used to
            // rebuild a HashSet and run three RemoveAll passes on every repaint of both zones.
            if (!_layoutDirty) return;
            _layoutDirty = false;

            var known = new HashSet<string>(_items.Select(i => i.Id));
            int before = _data.left.Count + _data.right.Count;

            _data.left.RemoveAll(id => !known.Contains(id));
            _data.right.RemoveAll(id => !known.Contains(id));
            _data.hidden.RemoveAll(id => !known.Contains(id));

            foreach (var item in _items)
            {
                if (_data.left.Contains(item.Id) || _data.right.Contains(item.Id)) continue;
                if (item.DefaultSide == ToolbarSide.Left) _data.left.Add(item.Id);
                else _data.right.Add(item.Id);
            }

            // Persist only if the sync actually changed placement (new/removed items).
            if (_data.left.Count + _data.right.Count != before) Save();
        }

        private static ToolbarItem FindItem(string id) =>
            _itemsById.TryGetValue(id, out ToolbarItem item) ? item : null;

        private static void MoveWithinSide(ToolbarSide side, string id, int delta)
        {
            var order = side == ToolbarSide.Left ? _data.left : _data.right;
            int idx = order.IndexOf(id);
            if (idx < 0) return;
            int newIdx = Mathf.Clamp(idx + delta, 0, order.Count - 1);
            if (newIdx == idx) return;
            order.RemoveAt(idx);
            order.Insert(newIdx, id);
            Save();
        }

        private static void MoveToSide(ToolbarSide fromSide, string id)
        {
            var from = fromSide == ToolbarSide.Left ? _data.left : _data.right;
            var to = fromSide == ToolbarSide.Left ? _data.right : _data.left;
            from.Remove(id);
            if (!to.Contains(id)) to.Add(id);
            Save();
        }

        private static void ResetLayout()
        {
            _data = new LayoutData();
            foreach (var item in _items)
            {
                if (item.DefaultSide == ToolbarSide.Left) _data.left.Add(item.Id);
                else _data.right.Add(item.Id);
            }

            _failedItems.Clear();
            Save();
        }

        #endregion

        static void DrawLeft() => DrawSide(ToolbarSide.Left, LeftToolbarGUI);
        static void DrawRight() => DrawSide(ToolbarSide.Right, RightToolbarGUI);

        private static void DrawSide(ToolbarSide side, List<Action> legacyHandlers)
        {
            SyncLayout();
            var order = side == ToolbarSide.Left ? _data.left : _data.right;

            GUILayout.BeginHorizontal();

            // Keep the grip nearest the center Play controls on both sides:
            // right zone -> grip first (its inner edge is on the left),
            // left zone  -> grip last  (its inner edge is on the right).
            if (side == ToolbarSide.Right) DrawGrip();

            foreach (var handler in legacyHandlers)
            {
                handler?.Invoke();
            }

            foreach (var id in order)
            {
                if (!IsEnabled(id) || _failedItems.Contains(id)) continue;

                ToolbarItem item = FindItem(id);
                if (item?.Draw == null) continue;

                DrawItem(item);
            }

            if (side == ToolbarSide.Left) DrawGrip();

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws one item, isolating a throwing item so it cannot take the rest of the toolbar with it.
        /// Unhandled, the same exception fires on every repaint and floods the console.
        /// </summary>
        private static void DrawItem(ToolbarItem item)
        {
            try
            {
                item.Draw();
            }
            catch (ExitGUIException)
            {
                // Legitimate IMGUI control flow (GUIUtility.ExitGUI) - must not be treated as a failure.
                throw;
            }
            catch (Exception e)
            {
                _failedItems.Add(item.Id);
                Debug.LogError($"[Toolbar] Item '{item.Label}' ({item.Id}) threw and has been disabled for " +
                               $"this session. Fix it and recompile to re-enable.\n{e}");
            }
        }

        private static GUIStyle _gripStyle;

        /// <summary>Built once; the toolbar repaints constantly and this used to allocate a style per frame.</summary>
        private static GUIStyle GripStyle
        {
            get
            {
                if (_gripStyle != null) return _gripStyle;

                _gripStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };
                _gripStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f);
                _gripStyle.hover.textColor = new Color(0.35f, 0.65f, 1f);
                _gripStyle.active.textColor = new Color(0.2f, 0.5f, 0.8f);

                return _gripStyle;
            }
        }

        private static readonly GUIContent GripContent = new GUIContent("\u22ee", "Toggle / arrange toolbar items");

        private static void DrawGrip()
        {
            GUILayout.Space(4);

            if (GUILayout.Button(GripContent, GripStyle, GUILayout.Width(12), GUILayout.Height(18)))
            {
                ShowLayoutMenu();
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            GUILayout.Space(2);
        }

        private static void ShowLayoutMenu()
        {
            SyncLayout();
            GenericMenu menu = new GenericMenu();

            BuildSideMenu(menu, ToolbarSide.Left, "Left", _data.left);
            BuildSideMenu(menu, ToolbarSide.Right, "Right", _data.right);

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Reset Layout"), false, ResetLayout);

            menu.ShowAsContext();
        }

        private static void BuildSideMenu(GenericMenu menu, ToolbarSide side, string sideLabel, List<string> order)
        {
            string otherLabel = side == ToolbarSide.Left ? "Right" : "Left";

            for (int i = 0; i < order.Count; i++)
            {
                var item = FindItem(order[i]);
                if (item == null) continue;

                string id = item.Id;
                string root = $"{sideLabel}/{item.Label}";
                bool isFirst = i == 0;
                bool isLast = i == order.Count - 1;

                menu.AddItem(new GUIContent($"{root}/Visible"), IsEnabled(id), () => SetEnabled(id, !IsEnabled(id)));
                menu.AddSeparator($"{root}/");

                if (!isFirst) menu.AddItem(new GUIContent($"{root}/Move Up"), false, () => MoveWithinSide(side, id, -1));
                else menu.AddDisabledItem(new GUIContent($"{root}/Move Up"));

                if (!isLast) menu.AddItem(new GUIContent($"{root}/Move Down"), false, () => MoveWithinSide(side, id, +1));
                else menu.AddDisabledItem(new GUIContent($"{root}/Move Down"));

                menu.AddItem(new GUIContent($"{root}/Move to {otherLabel}"), false, () => MoveToSide(side, id));
            }
        }
    }
}
