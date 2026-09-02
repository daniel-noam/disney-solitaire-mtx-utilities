using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Object = UnityEngine.Object;

namespace Utilities.Editor
{
    /// <summary>
    /// Browsing, searching and previewing the contents of an AssetBundle.
    ///
    /// Drawn with <see cref="ToolStyles"/>, so it follows the same rules as the rest of the toolset:
    /// two button levels, sizes from the shared scale, disabled states that are visibly disabled,
    /// and structural state frozen once per event pass.
    /// </summary>
    public class AssetBundleViewer : EditorWindow
    {
        /// <summary>Which column the list is ordered by. Direction is separate — see sortDescending.</summary>
        private enum SortKey { Name, Type, Size }

        private class AssetData
        {
            public string Name;
            public string Type;
            public long SizeBytes;
            public string SizeFormatted;
            public Object RawObject;
        }

        private AssetBundle loadedBundle;
        private string bundlePath = "";
        private readonly List<string> explicitAssets = new List<string>();
        private readonly List<AssetData> rawDependencies = new List<AssetData>();

        private List<AssetData> visibleAssets = new List<AssetData>();
        private long visibleMemoryBytes;
        private bool visibleAssetsDirty = true;

        private Vector2 listScroll;
        private string errorMessage = "";
        private bool showUnloadAllHint;
        private const string AllTypesLabel = "All Types";

        private string searchQuery = "";
        private SortKey sortKey = SortKey.Size;
        private bool sortDescending = true;
        private bool showComponents;
        private string typeFilter = AllTypesLabel;
        private string[] typeFilterValues = { AllTypesLabel };
        private string[] typeFilterLabels = { AllTypesLabel };
        private long minSizeFilterKb;

        private AssetData selectedAsset;
        private UnityEditor.Editor previewEditor;
        private bool isDragHovering;

        // Frozen for the event pass. IMGUI runs Layout and Repaint over the same code and requires
        // both to emit the same controls, and loading a bundle from a button in this very pass
        // replaces every list it draws from.
        private List<AssetData> frameRows = new List<AssetData>();
        private List<string> frameExplicit = new List<string>();
        private int rowsVersion = -1;
        private int contentVersion;
        private string frameError = "";
        private bool frameShowUnloadAll;
        private bool frameHasContent;

        /// <summary>Left column share of the window.</summary>
        private const float ListShare = 0.6f;

        [MenuItem("Utilities/AssetBundle Viewer", false, 1000)]
        public static void ShowWindow()
        {
            GetWindow<AssetBundleViewer>("AssetBundle Viewer").minSize = new Vector2(850, 550);
        }

        private void OnEnable()
        {
            // Without this, hover states only repaint when something else happens to trigger a
            // frame, and every button feels a step behind the pointer.
            wantsMouseMove = true;
        }

        private void OnDestroy() => UnloadCurrentBundle();

        // ---------- drawing ----------

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);

            if (Event.current.type == EventType.MouseMove) Repaint();
            if (Event.current.type == EventType.Layout) FreezeFrame();

            HandleDragAndDrop();

            GUILayout.Space(ToolStyles.SpaceL);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawBundleCard();
                    GUILayout.Space(ToolStyles.SpaceL);
                    DrawBody();
                    GUILayout.Space(ToolStyles.SpaceM);
                    DrawFooter();
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }
            GUILayout.Space(ToolStyles.SpaceL);

            if (isDragHovering) DrawDragOverlay();
        }

        private void FreezeFrame()
        {
            if (visibleAssetsDirty)
            {
                RebuildVisibleAssets();
                contentVersion++;
            }

            if (rowsVersion != contentVersion)
            {
                frameRows = new List<AssetData>(visibleAssets);
                frameExplicit = new List<string>(explicitAssets);
                rowsVersion = contentVersion;
            }

            frameError = errorMessage;
            frameShowUnloadAll = showUnloadAllHint;
            frameHasContent = explicitAssets.Count > 0 || rawDependencies.Count > 0;
        }

        private void DrawBundleCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var loaded = loadedBundle != null;
                var header = ToolStyles.CardHeader("Bundle");

                var reloadRect = new Rect(header.xMax - ToolStyles.ButtonM, header.y + 1,
                    ToolStyles.ButtonM, ToolStyles.ControlHeight);
                var browseRect = new Rect(reloadRect.x - ToolStyles.SpaceS - ToolStyles.ButtonM, header.y + 1,
                    ToolStyles.ButtonM, ToolStyles.ControlHeight);

                if (GUI.Button(browseRect, "Browse…", ToolStyles.Secondary))
                {
                    var startDir = StartDirectory();
                    var picked = EditorUtility.OpenFilePanel("Select AssetBundle", startDir, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        bundlePath = picked;
                        LoadBundleContents();
                    }
                }

                using (new ToolStyles.DisabledScope(string.IsNullOrEmpty(bundlePath)))
                {
                    if (GUI.Button(reloadRect, loaded ? "Reload" : "Load", ToolStyles.Secondary))
                        LoadBundleContents();
                }

                GUILayout.Space(ToolStyles.SpaceM);
                DrawDropZone(loaded);

                // What the bundle was explicitly built from — usually one prefab. It was a whole
                // list section with its own heading for a single line; it reads better as a caption
                // on the bundle it describes.
                if (frameExplicit.Count > 0)
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    var shown = Mathf.Min(frameExplicit.Count, 3);
                    for (var i = 0; i < shown; i++)
                    {
                        var line = GUILayoutUtility.GetRect(0, 15, GUILayout.ExpandWidth(true));
                        GUI.Label(line, ToolStyles.Elide(frameExplicit[i],
                            ToolStyles.MonoCharsFor(line.width)), ToolStyles.MonoSmall);
                    }
                    if (frameExplicit.Count > shown)
                        GUILayout.Label("and " + (frameExplicit.Count - shown) + " more assigned paths",
                            ToolStyles.Hint);
                }

                if (!string.IsNullOrEmpty(frameError))
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    EditorGUILayout.HelpBox(frameError, MessageType.Error);

                    // Bundles stay loaded across script recompiles even though this window loses its
                    // reference to them, which blocks re-loading the same file. Offered as an
                    // explicit opt-in rather than done behind the user's back on every load.
                    if (frameShowUnloadAll)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(new GUIContent("Unload all AssetBundles",
                                        "Affects the whole Editor session, not just this window."),
                                    ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonL),
                                    GUILayout.Height(ToolStyles.ControlHeight)))
                            {
                                AssetBundle.UnloadAllAssetBundles(true);
                                UnloadCurrentBundle();
                                errorMessage = "";
                                showUnloadAllHint = false;
                                LoadBundleContents();
                            }
                        }
                    }
                }
            }
        }

        private string StartDirectory()
        {
            try
            {
                if (File.Exists(bundlePath)) return Path.GetDirectoryName(bundlePath);
                if (Directory.Exists(bundlePath)) return bundlePath;
            }
            catch (Exception) { /* an unreadable path just means no starting directory */ }
            return "";
        }

        private void DrawDropZone(bool loaded)
        {
            var rect = GUILayoutUtility.GetRect(0, ToolStyles.DropZoneHeight, GUILayout.ExpandWidth(true));

            var fill = isDragHovering
                ? ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.Accent, 0.25f)
                : ToolStyles.InsetBg;

            EditorGUI.DrawRect(rect, fill);
            ToolStyles.DashedBorder(rect, isDragHovering ? ToolStyles.Accent : ToolStyles.Faint,
                5f, 4f, isDragHovering ? 2f : 1f);

            const float lineOne = 18f;
            const float lineTwo = 16f;
            var top = rect.y + (rect.height - (lineOne + lineTwo)) / 2f;
            var wide = new Rect(rect.x + 10, top, rect.width - 20, lineOne);
            var under = new Rect(rect.x + 10, top + lineOne, rect.width - 20, lineTwo);

            if (loaded || !string.IsNullOrEmpty(bundlePath))
            {
                GUI.Label(wide, new GUIContent(Path.GetFileName(bundlePath), bundlePath),
                    ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(under, new GUIContent(
                        ToolStyles.Elide(bundlePath, ToolStyles.MonoCharsFor(under.width)), bundlePath),
                    ToolStyles.Centred(ToolStyles.MonoSmall));
            }
            else
            {
                GUI.Label(wide, "Drag an AssetBundle here", ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(under, "or use Browse", ToolStyles.Centred(ToolStyles.Hint));
            }
        }

        private void DrawBody()
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.VerticalScope(ToolStyles.Card,
                           GUILayout.Width(position.width * ListShare), GUILayout.ExpandHeight(true)))
                    DrawContents();

                GUILayout.Space(ToolStyles.SpaceL);

                using (new EditorGUILayout.VerticalScope(ToolStyles.Card, GUILayout.ExpandHeight(true)))
                    DrawPreview();
            }
        }

        private void DrawContents()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Contents", ToolStyles.CardTitle);
                GUILayout.FlexibleSpace();

                using (new ToolStyles.DisabledScope(!HasActiveFilters))
                {
                    if (GUILayout.Button("Clear filters", ToolStyles.Secondary,
                            GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ControlHeight)))
                        ClearFilters();
                }
            }

            GUILayout.Space(ToolStyles.SpaceM);
            DrawFilters();
            GUILayout.Space(ToolStyles.SpaceM);

            var listRect = GUILayoutUtility.GetRect(0, 80,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));

            if (!frameHasContent)
            {
                GUI.Box(listRect, GUIContent.none, ToolStyles.Inset);
                GUI.Label(listRect, string.IsNullOrEmpty(bundlePath)
                        ? "No bundle loaded."
                        : string.IsNullOrEmpty(frameError) ? "This bundle is empty." : "",
                    ToolStyles.Centred(ToolStyles.Placeholder));
                return;
            }

            // Header and list share one inset panel, so the bar cannot sit as a square slab against
            // the panel's rounded corners.
            GUI.Box(listRect, GUIContent.none, ToolStyles.Inset);
            var inner = new Rect(listRect.x + 1, listRect.y + 1, listRect.width - 2, listRect.height - 2);

            var headerRect = new Rect(inner.x, inner.y, inner.width, ToolStyles.ControlHeight);
            var bodyRect = new Rect(inner.x, headerRect.yMax, inner.width,
                inner.height - headerRect.height);

            // One width for both, so the header cannot line up with the rows in one case and not
            // the other depending on whether the list happens to scroll.
            var contentWidth = ToolStyles.ListContentWidth(bodyRect, frameRows.Count,
                ToolStyles.ListRowHeight);

            DrawColumnHeader(headerRect, contentWidth);
            DrawList(bodyRect, contentWidth);
        }

        private void DrawFilters()
        {
            EditorGUI.BeginChangeCheck();
            searchQuery = EditorGUILayout.TextField(searchQuery, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) MarkFiltersDirty();

            GUILayout.Space(ToolStyles.SpaceS);

            using (new EditorGUILayout.HorizontalScope())
            {
                // The filters share a change check; the components toggle does not, because it does
                // not filter what is already loaded — it changes what gets loaded, so it re-reads
                // the bundle rather than rebuilding the list.
                EditorGUI.BeginChangeCheck();

                var typeIndex = Mathf.Max(0, Array.IndexOf(typeFilterValues, typeFilter));
                typeIndex = EditorGUILayout.Popup(typeIndex, typeFilterLabels,
                    GUILayout.Width(ToolStyles.FieldWidth));
                typeFilter = typeFilterValues[Mathf.Clamp(typeIndex, 0, typeFilterValues.Length - 1)];

                GUILayout.Space(ToolStyles.SpaceM);
                GUILayout.Label("Min KB", ToolStyles.RowLabel, GUILayout.Width(48));
                minSizeFilterKb = Math.Max(0, EditorGUILayout.LongField(minSizeFilterKb,
                    GUILayout.Width(ToolStyles.MetaWidth)));

                if (EditorGUI.EndChangeCheck()) MarkFiltersDirty();

                GUILayout.Space(ToolStyles.SpaceXL);

                var components = EditorGUILayout.ToggleLeft(new GUIContent("Show structural components",
                        "Include GameObjects and Components. They are usually noise, and deciding "
                        + "either way means re-reading the bundle, so this reloads it."),
                    showComponents, GUILayout.Width(196));
                if (components != showComponents)
                {
                    showComponents = components;
                    if (loadedBundle != null) LoadBundleContents();
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void ClearFilters()
        {
            searchQuery = "";
            typeFilter = AllTypesLabel;
            minSizeFilterKb = 0;
            GUI.FocusControl(null);
            MarkFiltersDirty();
        }

        private void MarkFiltersDirty() => visibleAssetsDirty = true;

        /// <summary>
        /// Where the three columns sit. Defined once so the clickable header and the rows beneath it
        /// cannot drift out of alignment.
        /// </summary>
        private static void Columns(Rect row, out Rect name, out Rect type, out Rect size)
        {
            const float sizeWidth = 74f;
            const float typeWidth = 110f;

            name = new Rect(row.x + 8, row.y,
                Mathf.Max(40f, row.width - 16 - sizeWidth - typeWidth), row.height);
            type = new Rect(name.xMax, row.y, typeWidth, row.height);
            size = new Rect(type.xMax, row.y, sizeWidth, row.height);
        }

        /// <summary>The sortable column header. Click a column to order by it, click again to flip.</summary>
        private void DrawColumnHeader(Rect rect, float contentWidth)
        {
            EditorGUI.DrawRect(rect, ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.CardBg, 0.6f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), ToolStyles.CardBorder);

            Columns(new Rect(rect.x, rect.y, contentWidth, rect.height),
                out var name, out var type, out var size);

            DrawColumnButton(name, "Name", SortKey.Name);
            DrawColumnButton(type, "Type", SortKey.Type);
            DrawColumnButton(size, "Size", SortKey.Size);
        }

        private void DrawColumnButton(Rect rect, string label, SortKey key)
        {
            var active = sortKey == key;
            var style = key == SortKey.Name ? ToolStyles.ColumnHeader : ToolStyles.ColumnHeaderRight;
            var text = label + (active ? sortDescending ? "  ▼" : "  ▲" : "");

            if (active) ToolStyles.ColouredLabel(rect, text, style, ToolStyles.Accent);
            else GUI.Label(rect, text, style);

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (!GUI.Button(rect, GUIContent.none, GUIStyle.none)) return;

            // Clicking the column already sorted flips it; a new column starts the way that column
            // is usually wanted — biggest first for size, A to Z for the two text ones.
            if (active) sortDescending = !sortDescending;
            else
            {
                sortKey = key;
                sortDescending = key == SortKey.Size;
            }
            MarkFiltersDirty();
        }

        private void DrawList(Rect rect, float contentWidth)
        {
            var content = new Rect(0, 0, contentWidth, frameRows.Count * ToolStyles.ListRowHeight);
            listScroll = GUI.BeginScrollView(rect, listScroll, content);

            // Only the rows on screen are drawn. A bundle can hold hundreds of assets, IMGUI charges
            // for every control whether or not it is visible, and this window now repaints on every
            // mouse move.
            var first = Mathf.Max(0, Mathf.FloorToInt(listScroll.y / ToolStyles.ListRowHeight));
            var last = Mathf.Min(frameRows.Count,
                first + Mathf.CeilToInt(rect.height / ToolStyles.ListRowHeight) + 1);

            for (var i = first; i < last; i++)
            {
                var row = new Rect(0, i * ToolStyles.ListRowHeight, content.width, ToolStyles.ListRowHeight);
                DrawAssetRow(row, frameRows[i], i);
            }

            GUI.EndScrollView();
        }

        private void DrawAssetRow(Rect rect, AssetData asset, int index)
        {
            var selected = selectedAsset == asset;

            if (selected)
            {
                // Laid over the row rather than lerped toward the panel colour: lerping washes the
                // accent out into a muddy blue-grey that reads as "slightly different" rather than
                // "this one". The solid edge does the rest of the work.
                var accent = ToolStyles.Accent;
                EditorGUI.DrawRect(rect, new Color(accent.r, accent.g, accent.b, 0.28f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), accent);
            }
            else if (index % 2 == 1)
            {
                EditorGUI.DrawRect(rect, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));
            }

            Columns(rect, out var nameRect, out var typeRect, out var sizeRect);

            GUI.Label(nameRect, ToolStyles.Elide(asset.Name, ToolStyles.MonoCharsFor(nameRect.width)),
                ToolStyles.MonoSmall);
            GUI.Label(typeRect, asset.Type, ToolStyles.StatusText);
            GUI.Label(sizeRect, asset.SizeFormatted, ToolStyles.StatusText);

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) SelectAssetForPreview(asset);
        }

        private void DrawPreview()
        {
            GUILayout.Label("Preview", ToolStyles.CardTitle);
            GUILayout.Space(ToolStyles.SpaceM);

            if (selectedAsset == null || selectedAsset.RawObject == null)
            {
                var empty = GUILayoutUtility.GetRect(0, 80,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                GUI.Box(empty, GUIContent.none, ToolStyles.Inset);
                GUI.Label(empty, "Select an asset to preview it.", ToolStyles.Centred(ToolStyles.Placeholder));
                return;
            }

            GUILayout.Label(new GUIContent(selectedAsset.Name, selectedAsset.Name), ToolStyles.CardTitle);
            GUILayout.Label(selectedAsset.Type + "  ·  " + selectedAsset.SizeFormatted, ToolStyles.Hint);
            ToolStyles.Divider();

            var previewRect = GUILayoutUtility.GetRect(0, 80,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            GUI.Box(previewRect, GUIContent.none, ToolStyles.Inset);

            if (previewEditor != null && previewEditor.HasPreviewGUI())
            {
                previewEditor.OnPreviewGUI(new Rect(previewRect.x + 1, previewRect.y + 1,
                    previewRect.width - 2, previewRect.height - 2), GUIStyle.none);
            }
            else
            {
                GUI.Label(previewRect, "No visual preview for this asset type.",
                    ToolStyles.Centred(ToolStyles.Placeholder));
            }
        }

        private void DrawFooter()
        {
            if (!frameHasContent) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                var count = HasActiveFilters
                    ? visibleAssets.Count + " of " + rawDependencies.Count + " assets"
                    : rawDependencies.Count + (rawDependencies.Count == 1 ? " asset" : " assets");
                if (ToolStyles.StatusPill(count, HasActiveFilters ? ToolStyles.Warn : ToolStyles.Muted,
                        HasActiveFilters ? "Filtered. Clear filters to see them all." : ""))
                    ClearFilters();

                GUILayout.FlexibleSpace();

                var memory = (HasActiveFilters ? "Filtered runtime memory  " : "Total runtime memory  ")
                             + EditorUtility.FormatBytes(visibleMemoryBytes);
                ToolStyles.StatusPill(memory, HasActiveFilters ? ToolStyles.Warn : ToolStyles.Accent,
                    "RAM and VRAM, as the Profiler reports it.");
            }
        }

        // ---------- drag and drop ----------

        private void HandleDragAndDrop()
        {
            var e = Event.current;

            if (e.type == EventType.DragExited || e.type == EventType.MouseLeaveWindow)
            {
                if (!isDragHovering) return;
                isDragHovering = false;
                Repaint();
                return;
            }

            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!new Rect(0, 0, position.width, position.height).Contains(e.mousePosition)) return;

            var dragged = FirstFile(DragAndDrop.paths);
            DragAndDrop.visualMode = dragged != null
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (isDragHovering != (dragged != null))
            {
                isDragHovering = dragged != null;
                Repaint();
            }

            if (e.type == EventType.DragPerform && dragged != null)
            {
                DragAndDrop.AcceptDrag();
                isDragHovering = false;
                bundlePath = dragged;
                LoadBundleContents();
            }
            e.Use();
        }

        private static string FirstFile(string[] paths)
        {
            if (paths == null) return null;
            foreach (var path in paths)
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            return null;
        }

        private void DrawDragOverlay()
        {
            var rect = new Rect(0, 0, position.width, position.height);
            EditorGUI.DrawRect(rect, new Color(ToolStyles.Accent.r, ToolStyles.Accent.g, ToolStyles.Accent.b, 0.12f));
            ToolStyles.DashedBorder(rect, ToolStyles.Accent, 8f, 6f, 3f);
            GUI.Label(rect, "Drop AssetBundle here", ToolStyles.Centred(ToolStyles.CardTitle));
        }

        // ---------- loading ----------

        private void SelectAssetForPreview(AssetData asset)
        {
            selectedAsset = asset;

            DestroyPreviewEditor();

            if (selectedAsset.RawObject != null)
            {
                previewEditor = UnityEditor.Editor.CreateEditor(selectedAsset.RawObject);
            }
        }

        private void LoadBundleContents()
        {
            if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
            {
                errorMessage = "Please select a valid file path.";
                showUnloadAllHint = false;
                UnloadCurrentBundle();
                return;
            }

            errorMessage = "";
            showUnloadAllHint = false;
            UnloadCurrentBundle();

            loadedBundle = AssetBundle.LoadFromFile(bundlePath);
            if (loadedBundle == null)
            {
                errorMessage = "Failed to load AssetBundle. The file might not be a valid AssetBundle, it might be built " +
                               "for an incompatible platform, or another copy of it is still loaded in this Editor session.";
                showUnloadAllHint = AssetBundle.GetAllLoadedAssetBundles().Any();
                return;
            }

            explicitAssets.AddRange(loadedBundle.GetAllAssetNames());
            explicitAssets.AddRange(loadedBundle.GetAllScenePaths());

            // LoadAllAssets throws on streamed scene bundles - those only expose scene paths.
            if (!loadedBundle.isStreamedSceneAssetBundle)
            {
                try
                {
                    CollectDependencies(loadedBundle.LoadAllAssets());
                }
                catch (Exception e)
                {
                    errorMessage = $"Loaded the bundle but failed to read its assets: {e.Message}";
                }
            }

            RebuildTypeFilterOptions();
            visibleAssetsDirty = true;
        }

        private void CollectDependencies(Object[] mainObjects)
        {
            Object[] deepDependencies = EditorUtility.CollectDependencies(mainObjects);
            HashSet<int> seenInstanceIds = new HashSet<int>();

            foreach (Object obj in deepDependencies)
            {
                if (obj == null) continue;

                if (!showComponents && (obj is GameObject || obj is Component))
                    continue;

                // Dedupe on identity: distinct assets can legitimately share a name and type,
                // and double-counting them would inflate the memory total.
                if (!seenInstanceIds.Add(obj.GetInstanceID())) continue;

                long size = Profiler.GetRuntimeMemorySizeLong(obj);

                rawDependencies.Add(new AssetData
                {
                    Name = string.IsNullOrEmpty(obj.name) ? "Unnamed" : obj.name,
                    Type = obj.GetType().Name,
                    SizeBytes = size,
                    SizeFormatted = EditorUtility.FormatBytes(size),
                    RawObject = obj
                });
            }
        }

        private void RebuildVisibleAssets()
        {
            visibleAssetsDirty = false;

            IEnumerable<AssetData> filtered = rawDependencies;
            if (!string.IsNullOrEmpty(searchQuery))
            {
                filtered = filtered.Where(x => Matches(x.Name) || Matches(x.Type));
            }
            if (typeFilter != AllTypesLabel)
            {
                filtered = filtered.Where(x => x.Type == typeFilter);
            }
            if (minSizeFilterKb > 0)
            {
                long minSizeBytes = minSizeFilterKb * 1024;
                filtered = filtered.Where(x => x.SizeBytes >= minSizeBytes);
            }

            switch (sortKey)
            {
                case SortKey.Name:
                    filtered = sortDescending
                        ? filtered.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                        : filtered.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortKey.Type:
                    // Ties broken by size: within one type, the big ones are what you came to find.
                    filtered = sortDescending
                        ? filtered.OrderByDescending(x => x.Type, StringComparer.OrdinalIgnoreCase)
                            .ThenByDescending(x => x.SizeBytes)
                        : filtered.OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
                            .ThenByDescending(x => x.SizeBytes);
                    break;
                default:
                    filtered = sortDescending
                        ? filtered.OrderByDescending(x => x.SizeBytes)
                        : filtered.OrderBy(x => x.SizeBytes);
                    break;
            }

            visibleAssets = filtered.ToList();
            visibleMemoryBytes = visibleAssets.Sum(x => x.SizeBytes);
        }

        private bool HasActiveFilters =>
            !string.IsNullOrEmpty(searchQuery) || typeFilter != AllTypesLabel || minSizeFilterKb > 0;

        private void RebuildTypeFilterOptions()
        {
            var types = rawDependencies
                .Select(x => x.Type)
                .GroupBy(x => x)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            typeFilterValues = new[] { AllTypesLabel }
                .Concat(types.Select(g => g.Key))
                .ToArray();
            typeFilterLabels = new[] { $"{AllTypesLabel} ({rawDependencies.Count})" }
                .Concat(types.Select(g => $"{g.Key} ({g.Count()})"))
                .ToArray();

            // A type selected for the previous bundle may not exist in this one.
            if (!typeFilterValues.Contains(typeFilter)) typeFilter = AllTypesLabel;
        }

        private bool Matches(string value)
        {
            return value != null && value.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UnloadCurrentBundle()
        {
            DestroyPreviewEditor();

            selectedAsset = null;
            explicitAssets.Clear();
            rawDependencies.Clear();
            visibleAssets.Clear();
            visibleMemoryBytes = 0;
            visibleAssetsDirty = true;
            typeFilterValues = new[] { AllTypesLabel };
            typeFilterLabels = new[] { AllTypesLabel };

            if (loadedBundle != null)
            {
                loadedBundle.Unload(true);
                loadedBundle = null;
            }
        }

        private void DestroyPreviewEditor()
        {
            if (previewEditor != null)
            {
                DestroyImmediate(previewEditor);
                previewEditor = null;
            }
        }

    }
}
