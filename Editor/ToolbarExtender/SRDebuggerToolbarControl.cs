using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    [InitializeOnLoad]
    public static class SRDebuggerToolbarControl
    {
        public enum CheatType { MethodButton, PropertyToggle, NumericSlider, StringTextField }

        [Serializable]
        public class CheatDefinition 
        { 
            public string Name; 
            public string Label; 
            public CheatType Type; 
            public float Min = 0f; 
            public float Max = 1f; 
        }

        public class LiveOptionData
        {
            public string Name;
            public string Category;
            public CheatType Type;
            public float Min = 0f;
            public float Max = 1f;
            public object RawOptionReference;
        }

        [Serializable]
        private class CheatListWrapper { public List<CheatDefinition> Cheats = new List<CheatDefinition>(); }

        private static List<CheatDefinition> _registeredCheats = new List<CheatDefinition>();
        private const string SavePrefsKey = "SRDebuggerToolbar_CheatsData";

        private const string ServiceManagerTypeName = "SRF.Service.SRServiceManager";
        private const string OptionsServiceTypeName = "SRDebugger.Services.IOptionsService";

        /// <summary>
        /// How long a snapshot of SRDebugger's option list stays valid. Building it is a deep reflection
        /// sweep, while reading each option's current value from the snapshot is cheap - so the structure
        /// is refreshed a couple of times a second and the values are still read live.
        /// </summary>
        private const double LiveOptionsCacheSeconds = 0.5;

        private static Type _serviceManagerType;
        private static Type _optionsServiceType;
        private static bool _typesResolved;

        private static List<LiveOptionData> _liveOptions = new List<LiveOptionData>();
        private static double _liveOptionsExpiry;

        static SRDebuggerToolbarControl()
        {
            LoadCheatsSavedData();
            ToolbarExtender.RegisterLeft("srdebugger", "SRDebugger Cheats", DrawDebuggerControls);
        }

        /// <summary>Resolved once per domain; a recompile retries, which is when the answer can change.</summary>
        private static bool CheckSRDebuggerAvailability()
        {
            if (!_typesResolved)
            {
                _typesResolved = true;
                _serviceManagerType = FindTypeEverywhere(ServiceManagerTypeName);
                _optionsServiceType = FindTypeEverywhere(OptionsServiceTypeName);
            }

            return _serviceManagerType != null && _optionsServiceType != null;
        }

        /// <summary>
        /// The cached option snapshot, rebuilt when stale. Every value getter used to call
        /// GetLiveSRDebuggerOptions() itself, so drawing N cheats meant N full sweeps of the option
        /// collection on every single toolbar repaint.
        /// </summary>
        private static List<LiveOptionData> GetCachedLiveOptions()
        {
            if (EditorApplication.timeSinceStartup >= _liveOptionsExpiry)
            {
                _liveOptionsExpiry = EditorApplication.timeSinceStartup + LiveOptionsCacheSeconds;
                _liveOptions = GetLiveSRDebuggerOptions();
            }

            return _liveOptions;
        }

        private static LiveOptionData FindLiveOption(string name) =>
            GetCachedLiveOptions().Find(o => o.Name == name);

        public static void RegisterCheat(string name, string label, CheatType type, float min = 0f, float max = 1f)
        {
            _registeredCheats.RemoveAll(c => c.Name == name);
            _registeredCheats.Add(new CheatDefinition { Name = name, Label = label, Type = type, Min = min, Max = max });
            SaveCheatsData();
        }

        public static void UnregisterCheat(string name)
        {
            _registeredCheats.RemoveAll(c => c.Name == name);
            SaveCheatsData();
        }

        // Styles are built once. These are drawn from the toolbar, which repaints continuously, so
        // allocating a GUIStyle inline meant one per style per frame.
        private static GUIStyle _labelStyle;
        private static GUIStyle _disabledLabelStyle;

        private static GUIStyle LabelStyle => _labelStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold
        };

        private static GUIStyle DisabledLabelStyle => _disabledLabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.gray }
        };

        private static void DrawDebuggerControls()
        {
            // Early exit if SRDebugger plugin isn't present in the project assembly at all
            if (!CheckSRDebuggerAvailability()) return;

            GUILayout.Space(10);

            if (!EditorApplication.isPlaying)
            {
                GUILayout.Label("SRDebug: (Play Mode Only)", DisabledLabelStyle, GUILayout.Width(140), GUILayout.Height(18));
                return;
            }

            GUILayout.Label("SRDebug:", LabelStyle, GUILayout.Width(55), GUILayout.Height(18));

            GUILayout.Space(2);

            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(20), GUILayout.Height(18)))
            {
                CheatEditorWindow.OpenForAdd();
            }

            // Loop and draw custom dynamic elements sequentially across the top bar
            foreach (var cheat in _registeredCheats)
            {
                GUILayout.Space(5);

                if (cheat.Type == CheatType.MethodButton)
                {
                    float width = EditorStyles.miniButton.CalcSize(new GUIContent(cheat.Label)).x + 10;
                    Rect btnRect = GUILayoutUtility.GetRect(width, 18);
                    btnRect.y += 2;

                    if (CheckRightClick(btnRect)) ShowContextMenu(cheat);
                    else if (GUI.Button(btnRect, cheat.Label, EditorStyles.miniButton)) InvokeLiveOption(cheat.Name);
                }
                else if (cheat.Type == CheatType.PropertyToggle)
                {
                    bool currentState = GetLiveOptionBool(cheat.Name);
                    float toggleWidth = GUI.skin.toggle.CalcSize(new GUIContent(cheat.Label)).x + 20;
                    Rect toggleRect = GUILayoutUtility.GetRect(toggleWidth, 22);

                    if (CheckRightClick(toggleRect)) ShowContextMenu(cheat);
                    else
                    {
                        EditorGUI.BeginChangeCheck();
                        bool newState = GUI.Toggle(toggleRect, currentState, cheat.Label);
                        if (EditorGUI.EndChangeCheck()) SetLiveOptionBool(cheat.Name, newState);
                    }
                }
                else if (cheat.Type == CheatType.NumericSlider)
                {
                    float currentVal = GetLiveOptionFloat(cheat.Name);
                    float labelWidth = EditorStyles.miniLabel.CalcSize(new GUIContent($"{cheat.Label}: 00.0")).x + 5;
                    float totalWidth = labelWidth + 75;
                    
                    Rect blockRect = GUILayoutUtility.GetRect(totalWidth, 22);

                    if (CheckRightClick(blockRect)) ShowContextMenu(cheat);
                    else
                    {
                        Rect labelRect = new Rect(blockRect.x, blockRect.y + 2, labelWidth, 18);
                        Rect sliderRect = new Rect(blockRect.x + labelWidth, blockRect.y + 4, 70, 18);

                        GUI.Label(labelRect, $"{cheat.Label}: {currentVal:F1}", EditorStyles.miniLabel);
                        
                        EditorGUI.BeginChangeCheck();
                        float newVal = GUI.HorizontalSlider(sliderRect, currentVal, cheat.Min, cheat.Max);
                        if (EditorGUI.EndChangeCheck()) SetLiveOptionFloat(cheat.Name, newVal);
                    }
                }
                else if (cheat.Type == CheatType.StringTextField)
                {
                    string currentStr = GetLiveOptionString(cheat.Name);
                    float labelWidth = EditorStyles.miniLabel.CalcSize(new GUIContent($"{cheat.Label}: ")).x + 3;
                    float totalWidth = labelWidth + 85;

                    Rect blockRect = GUILayoutUtility.GetRect(totalWidth, 22);

                    if (CheckRightClick(blockRect)) ShowContextMenu(cheat);
                    else
                    {
                        Rect labelRect = new Rect(blockRect.x, blockRect.y + 2, labelWidth, 18);
                        Rect fieldRect = new Rect(blockRect.x + labelWidth, blockRect.y + 2, 80, 18);

                        GUI.Label(labelRect, $"{cheat.Label}:", EditorStyles.miniLabel);

                        EditorGUI.BeginChangeCheck();
                        string newStr = GUI.TextField(fieldRect, currentStr, EditorStyles.textField);
                        if (EditorGUI.EndChangeCheck()) SetLiveOptionString(cheat.Name, newStr);
                    }
                }
            }
        }

        private static bool CheckRightClick(Rect targetRect)
        {
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                if (targetRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    return true;
                }
            }
            return false;
        }

        private static void ShowContextMenu(CheatDefinition targetCheat)
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("Edit"), false, () => CheatEditorWindow.OpenForEdit(targetCheat.Name, targetCheat.Label, targetCheat.Type, targetCheat.Min, targetCheat.Max));
            menu.AddItem(new GUIContent("Remove"), false, () => UnregisterCheat(targetCheat.Name));
            menu.ShowAsContext();
        }

        #region Robust Parameter-Safe Reflection Engine

        private static MethodInfo GetMethodWithParamCount(Type type, string methodName, int expectedParamCount)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.Name == methodName && method.GetParameters().Length == expectedParamCount) return method;
            }
            return null;
        }

        public static List<LiveOptionData> GetLiveSRDebuggerOptions()
        {
            var list = new List<LiveOptionData>();
            if (!EditorApplication.isPlaying || !CheckSRDebuggerAvailability()) return list;

            try
            {
                Type serviceManagerType = _serviceManagerType;
                Type optionsServiceType = _optionsServiceType;

                if (serviceManagerType == null || optionsServiceType == null) return list;

                var getServiceMethod = serviceManagerType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var optionsServiceInstance = getServiceMethod?.MakeGenericMethod(optionsServiceType)?.Invoke(null, null);

                if (optionsServiceInstance == null) return list;

                var optionsCollection = optionsServiceType.GetProperty("Options", BindingFlags.Public | BindingFlags.Instance)?.GetValue(optionsServiceInstance) as System.Collections.IEnumerable;
                if (optionsCollection == null) return list;

                foreach (var opt in optionsCollection)
                {
                    Type opType = opt.GetType();
                    string name = opType.GetProperty("Name")?.GetValue(opt) as string;
                    string category = opType.GetProperty("Category")?.GetValue(opt) as string;
                    
                    var propWrapper = opType.GetProperty("Property")?.GetValue(opt) ?? opType.GetField("Property")?.GetValue(opt);
                    
                    CheatType cType = CheatType.MethodButton;
                    float min = 0f;
                    float max = 1f;

                    if (propWrapper != null)
                    {
                        PropertyInfo pInfo = null;
                        FieldInfo fInfo = null;

                        foreach (var prop in propWrapper.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (prop.PropertyType == typeof(PropertyInfo)) pInfo = prop.GetValue(propWrapper) as PropertyInfo;
                            if (prop.PropertyType == typeof(FieldInfo)) fInfo = prop.GetValue(propWrapper) as FieldInfo;
                        }
                        foreach (var field in propWrapper.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (field.FieldType == typeof(PropertyInfo)) pInfo = field.GetValue(propWrapper) as PropertyInfo;
                            if (field.FieldType == typeof(FieldInfo)) fInfo = field.GetValue(propWrapper) as FieldInfo;
                        }

                        Type valueType = pInfo != null ? pInfo.PropertyType : (fInfo != null ? fInfo.FieldType : null);

                        if (valueType == typeof(bool)) cType = CheatType.PropertyToggle;
                        else if (valueType == typeof(string)) cType = CheatType.StringTextField;
                        else if (valueType == typeof(float) || valueType == typeof(int) || valueType == typeof(double))
                        {
                            cType = CheatType.NumericSlider;

                            object[] attributes = pInfo != null ? pInfo.GetCustomAttributes(true) : (fInfo != null ? fInfo.GetCustomAttributes(true) : null);
                            if (attributes != null)
                            {
                                foreach (var attr in attributes)
                                {
                                    string tName = attr.GetType().Name;
                                    if (tName.Contains("NumberRange") || tName.Contains("Slider"))
                                    {
                                        min = Convert.ToSingle(attr.GetType().GetField("Min")?.GetValue(attr) ?? attr.GetType().GetProperty("Min")?.GetValue(attr) ?? 0f);
                                        max = Convert.ToSingle(attr.GetType().GetField("Max")?.GetValue(attr) ?? attr.GetType().GetProperty("Max")?.GetValue(attr) ?? 1f);
                                    }
                                }
                            }
                            // Guarded: name is a reflected property and can be null, which used to
                            // throw here. OrdinalIgnoreCase avoids a per-option lowercase copy.
                            if (name != null && name.IndexOf("timescale", StringComparison.OrdinalIgnoreCase) >= 0
                                && min == 0f && max == 1f) { min = 0f; max = 5f; }
                        }
                        else continue; 
                    }

                    list.Add(new LiveOptionData { Name = name, Category = category, Type = cType, Min = min, Max = max, RawOptionReference = opt });
                }
            }
            catch (Exception e) { Debug.LogException(e); }

            return list;
        }

        private static void InvokeLiveOption(string name)
        {
            var match = FindLiveOption(name);
            if (match == null) return;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef) ??
                        optType.GetProperty("Container")?.GetValue(optRef) ?? optType.GetField("Container")?.GetValue(optRef);

            var methodValue = optType.GetProperty("Method")?.GetValue(optRef) ?? optType.GetField("Method")?.GetValue(optRef);

            if (methodValue != null)
            {
                foreach (var m in methodValue.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "Invoke")
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length == 0) { m.Invoke(methodValue, null); return; }
                        if (parameters.Length == 2) { m.Invoke(methodValue, new object[] { owner, null }); return; }
                        if (parameters.Length == 1) { m.Invoke(methodValue, new object[] { owner }); return; }
                    }
                }
            }
        }

        private static bool GetLiveOptionBool(string name)
        {
            if (!EditorApplication.isPlaying) return false;

            var match = FindLiveOption(name);
            if (match == null) return false;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                var getValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 0);
                if (getValueMethod != null) { var r = getValueMethod.Invoke(propWrapper, null); if (r is bool b) return b; }
                
                var getValueMethodWithParam = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 1);
                if (getValueMethodWithParam != null && owner != null) { var r = getValueMethodWithParam.Invoke(propWrapper, new object[] { owner }); if (r is bool b) return b; }
            }
            return false;
        }

        private static void SetLiveOptionBool(string name, bool value)
        {
            if (!EditorApplication.isPlaying) return;

            var match = FindLiveOption(name);
            if (match == null) return;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                var setValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 1);
                if (setValueMethod != null) { setValueMethod.Invoke(propWrapper, new object[] { value }); return; }
                var setValueMethodWith2 = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 2);
                if (setValueMethodWith2 != null && owner != null) setValueMethodWith2.Invoke(propWrapper, new object[] { owner, value });
            }
        }

        private static float GetLiveOptionFloat(string name)
        {
            if (!EditorApplication.isPlaying) return 0f;

            var match = FindLiveOption(name);
            if (match == null) return 0f;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                var getValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 0);
                if (getValueMethod != null) { var r = getValueMethod.Invoke(propWrapper, null); if (r != null) return Convert.ToSingle(r); }

                var getValueMethodWithParam = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 1);
                if (getValueMethodWithParam != null && owner != null) { var r = getValueMethodWithParam.Invoke(propWrapper, new object[] { owner }); if (r != null) return Convert.ToSingle(r); }
            }
            return 0f;
        }

        private static void SetLiveOptionFloat(string name, float value)
        {
            if (!EditorApplication.isPlaying) return;

            var match = FindLiveOption(name);
            if (match == null) return;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                PropertyInfo pInfo = null;
                FieldInfo fInfo = null;

                foreach (var prop in propWrapper.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (prop.PropertyType == typeof(PropertyInfo)) pInfo = prop.GetValue(propWrapper) as PropertyInfo;
                foreach (var field in propWrapper.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (field.FieldType == typeof(FieldInfo)) fInfo = field.GetValue(propWrapper) as FieldInfo;

                Type targetType = pInfo != null ? pInfo.PropertyType : (fInfo != null ? fInfo.FieldType : typeof(float));
                object convertedValue = Convert.ChangeType(value, targetType);

                var setValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 1);
                if (setValueMethod != null) { setValueMethod.Invoke(propWrapper, new object[] { convertedValue }); return; }
                
                var setValueMethodWith2 = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 2);
                if (setValueMethodWith2 != null && owner != null) setValueMethodWith2.Invoke(propWrapper, new object[] { owner, convertedValue });
            }
        }

        private static string GetLiveOptionString(string name)
        {
            if (!EditorApplication.isPlaying) return "";

            var match = FindLiveOption(name);
            if (match == null) return "";

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                var getValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 0);
                if (getValueMethod != null) { var r = getValueMethod.Invoke(propWrapper, null); if (r != null) return r.ToString(); }

                var getValueMethodWithParam = GetMethodWithParamCount(propWrapper.GetType(), "GetValue", 1);
                if (getValueMethodWithParam != null && owner != null) { var r = getValueMethodWithParam.Invoke(propWrapper, new object[] { owner }); if (r != null) return r.ToString(); }
            }
            return "";
        }

        private static void SetLiveOptionString(string name, string value)
        {
            if (!EditorApplication.isPlaying) return;

            var match = FindLiveOption(name);
            if (match == null) return;

            object optRef = match.RawOptionReference;
            Type optType = optRef.GetType();
            var owner = optType.GetProperty("Owner")?.GetValue(optRef) ?? optType.GetField("Owner")?.GetValue(optRef);
            var propWrapper = optType.GetProperty("Property")?.GetValue(optRef) ?? optType.GetField("Property")?.GetValue(optRef);

            if (propWrapper != null)
            {
                var setValueMethod = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 1);
                if (setValueMethod != null) { setValueMethod.Invoke(propWrapper, new object[] { value }); return; }
                
                var setValueMethodWith2 = GetMethodWithParamCount(propWrapper.GetType(), "SetValue", 2);
                if (setValueMethodWith2 != null && owner != null) setValueMethodWith2.Invoke(propWrapper, new object[] { owner, value });
            }
        }

        private static Type FindTypeEverywhere(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = assembly.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        #endregion

        #region Storage System

        private static void SaveCheatsData()
        {
            CheatListWrapper wrapper = new CheatListWrapper { Cheats = _registeredCheats };
            EditorPrefs.SetString(SavePrefsKey, JsonUtility.ToJson(wrapper));
        }

        private static void LoadCheatsSavedData()
        {
            if (EditorPrefs.HasKey(SavePrefsKey))
            {
                try
                {
                    CheatListWrapper wrapper = JsonUtility.FromJson<CheatListWrapper>(EditorPrefs.GetString(SavePrefsKey));
                    if (wrapper != null && wrapper.Cheats != null) { _registeredCheats = wrapper.Cheats; return; }
                }
                catch { }
            }
            _registeredCheats = new List<CheatDefinition>();
        }

        #endregion
    }
}