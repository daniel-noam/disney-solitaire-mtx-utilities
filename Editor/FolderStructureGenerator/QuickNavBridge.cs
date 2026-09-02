using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Soft, reflection-based integration with the optional QuickNavigation tool.
    ///
    /// QuickNavigation is not part of this tool's assembly (it lives in each host project), so we
    /// bind to its public API by reflection at runtime. When it is absent, <see cref="IsAvailable"/>
    /// is false and every call is a no-op — the folder generator keeps working on its own.
    /// </summary>
    public static class QuickNavBridge
    {
        private static bool _resolved;
        private static Type _dataType;
        private static Type _tabType;
        private static PropertyInfo _instanceProp;
        private static FieldInfo _favoriteTabsField;
        private static FieldInfo _tabNameField;
        private static FieldInfo _tabColorField;
        private static MethodInfo _addToFavoritesMethod;
        private static MethodInfo _saveMethod;
        private static MethodInfo _tabGetEntriesMethod;

        public static bool IsAvailable
        {
            get
            {
                ResolveApi();
                return _dataType != null && _tabType != null &&
                       _instanceProp != null && _favoriteTabsField != null &&
                       _tabNameField != null && _addToFavoritesMethod != null && _saveMethod != null;
            }
        }

        private static void ResolveApi()
        {
            if (_resolved) return;
            _resolved = true;

            _dataType = FindType("QuickNavigationData");
            _tabType = FindType("FavoriteTab");
            if (_dataType == null || _tabType == null) return;

            _instanceProp = _dataType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _favoriteTabsField = _dataType.GetField("FavoriteTabs", BindingFlags.Public | BindingFlags.Instance);
            _addToFavoritesMethod = _dataType.GetMethod("AddToFavorites", new[] { typeof(string), typeof(int) });
            _saveMethod = _dataType.GetMethod("Save", Type.EmptyTypes);
            _tabNameField = _tabType.GetField("TabName", BindingFlags.Public | BindingFlags.Instance);
            _tabColorField = _tabType.GetField("TabColor", BindingFlags.Public | BindingFlags.Instance);

            // Optional: lets AddFoldersToTab report how many favorites were actually added.
            _tabGetEntriesMethod = _tabType.GetMethod("GetEntries", Type.EmptyTypes);
        }

        private static Type FindType(string simpleName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(simpleName);
                if (t != null) return t;
            }

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (Type t in types)
                    if (t.Name == simpleName) return t;
            }

            return null;
        }

        /// <summary>
        /// Adds the given folder paths to a QuickNav favorites tab, creating the tab if needed.
        /// Returns how many favorites were actually added, or 0 when QuickNav is unavailable.
        /// </summary>
        public static int AddFoldersToTab(string tabName, Color tabColor, IEnumerable<string> paths)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(tabName) || paths == null) return 0;

            try
            {
                object data = _instanceProp.GetValue(null);
                if (data == null) return 0;

                if (!(_favoriteTabsField.GetValue(data) is IList tabs)) return 0;

                int tabIndex = -1;
                for (int i = 0; i < tabs.Count; i++)
                {
                    if (_tabNameField.GetValue(tabs[i]) as string == tabName)
                    {
                        tabIndex = i;
                        break;
                    }
                }

                if (tabIndex < 0)
                {
                    object tab = Activator.CreateInstance(_tabType);
                    _tabNameField.SetValue(tab, tabName);
                    _tabColorField?.SetValue(tab, tabColor);
                    tabs.Add(tab);
                    tabIndex = tabs.Count - 1;
                }

                // AddToFavorites silently ignores paths it can't add or that the tab already has, so the
                // number of calls is not the number of favorites gained - measure the tab instead.
                int before = CountTabEntries(tabs[tabIndex]);
                int attempted = 0;

                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    _addToFavoritesMethod.Invoke(data, new object[] { path, tabIndex });
                    attempted++;
                }

                _saveMethod.Invoke(data, null);

                int after = CountTabEntries(tabs[tabIndex]);
                return before < 0 || after < 0 ? attempted : after - before;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Folder Structure] QuickNav integration failed: {e.Message}");
                return 0;
            }
        }

        /// <summary>Entry count for a favorites tab, or -1 when QuickNav doesn't expose one.</summary>
        private static int CountTabEntries(object tab)
        {
            if (_tabGetEntriesMethod == null || tab == null) return -1;
            return _tabGetEntriesMethod.Invoke(tab, null) is ICollection entries ? entries.Count : -1;
        }
    }
}
