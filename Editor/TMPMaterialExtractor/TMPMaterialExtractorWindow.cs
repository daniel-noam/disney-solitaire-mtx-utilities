using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>Editor window for extracting TMP materials from selection and assigning them to scene objects.</summary>
    public class TMPMaterialExtractorWindow : EditorWindow
    {
        public enum MaterialType
        {
            Title,
            SubTitle,
            GrandPrize,
            Other
        }

        /// <summary>Maps a scene TMP object to a material asset for batch assignment.</summary>
        [Serializable]
        public class AssignmentItem
        {
            public GameObject tmpGameObject;
            public Material materialToAssign;

            public TMP_Text TMP => tmpGameObject ? tmpGameObject.GetComponent<TMP_Text>() : null;

            /// <summary>True when this row has everything it needs to be applied.</summary>
            public bool IsReady => TMP != null && materialToAssign != null;
        }

        /// <summary>One material that <see cref="CreateMaterials"/> would produce, used for the live preview.</summary>
        private readonly struct PlannedMaterial
        {
            public TMP_Text Source { get; }
            public string AssetPath { get; }

            public PlannedMaterial(TMP_Text source, string assetPath)
            {
                Source = source;
                AssetPath = assetPath;
            }
        }

        private const string DefaultRootFolder = "Assets/Fonts Assets";
        private const int MaxPreviewRows = 6;
        private const float RowIconWidth = 20f;
        private const float RowButtonWidth = 22f;

        // A single stored path, rather than the previous folder ObjectField + separate "fallback" string +
        // read-only "resolved path" label, which were three controls for one value with unclear precedence.
        [SerializeField] private string rootFolderPath = DefaultRootFolder;

        /// <summary>Optional middle segment of the material name. Blank simply leaves it out.</summary>
        [SerializeField] private string namePrefix = "";
        [SerializeField] private MaterialType materialType = MaterialType.Title;
        [SerializeField] private string customTypeName = "";

        [SerializeField] private bool autoAddCreatedToAssignments = true;
        [SerializeField] private bool autoAssignCreatedToSelection = false;

        [SerializeField] private List<AssignmentItem> assignments = new List<AssignmentItem>();

        [SerializeField]
        private Vector2 scroll;
        private bool rootDragHover;

        // Refreshed on selection change instead of walking the selection every repaint.
        private readonly List<TMP_Text> selectedTmps = new List<TMP_Text>();

        private string statusMessage;
        private MessageType statusType = MessageType.Info;

        [MenuItem("Utilities/Material Extractor & Assigner", false, 1008)]
        public static void Open()
        {
            GetWindow<TMPMaterialExtractorWindow>("Material Extractor & Assigner").minSize = new Vector2(460, 420);
        }

        private void OnEnable()
        {
            // Without this, hover states only repaint when something else triggers a frame.
            wantsMouseMove = true;
            RefreshSelection();
        }
        private void OnFocus() => RefreshSelection();

        private void OnSelectionChange()
        {
            RefreshSelection();
            Repaint();
        }

        private void RefreshSelection()
        {
            selectedTmps.Clear();

            foreach (GameObject go in Selection.gameObjects)
            {
                if (!go) continue;

                foreach (TMP_Text tmp in go.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (tmp && !selectedTmps.Contains(tmp)) selectedTmps.Add(tmp);
                }
            }
        }

        // ---- GUI ---------------------------------------------------------------------------------

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            EditorGUIUtility.labelWidth = 110;

            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                GUILayout.Space(ToolStyles.SpaceL);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ToolStyles.SpaceL);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        DrawCreateSection();
                        GUILayout.Space(ToolStyles.SpaceL);
                        DrawAssignSection();
                    }
                    GUILayout.Space(ToolStyles.SpaceL);
                }

                GUILayout.Space(ToolStyles.SpaceL);
            }

            DrawStatusBar();
        }


        private void DrawCreateSection()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var header = ToolStyles.CardHeader("Create from selection");

                var browseRect = new Rect(header.xMax - ToolStyles.ButtonM, header.y + 1,
                    ToolStyles.ButtonM, ToolStyles.ControlHeight);
                if (GUI.Button(browseRect, "Browse…", ToolStyles.Secondary)) PickRootFolder();

                GUILayout.Space(ToolStyles.SpaceS);
                GUILayout.Label("One material asset per selected TMP object, copied from its current "
                    + "material and named Font_Prefix_Type. Assets go to RootFolder/Type/, and a name "
                    + "that already exists gets a numeric suffix.", ToolStyles.Hint);
                GUILayout.Space(ToolStyles.SpaceM);

                DrawRootFolderField();

                namePrefix = EditorGUILayout.TextField(new GUIContent("Prefix",
                    "Optional. Sits between the font asset and the type in the material name."),
                    namePrefix);
                materialType = (MaterialType)EditorGUILayout.EnumPopup("Type", materialType);

                if (materialType == MaterialType.Other)
                    customTypeName = EditorGUILayout.TextField("Custom type", customTypeName);

                GUILayout.Space(ToolStyles.SpaceM);
                DrawPlan();

                GUILayout.Space(ToolStyles.SpaceM);
                autoAddCreatedToAssignments = EditorGUILayout.ToggleLeft(
                    "Add created materials to the assign list", autoAddCreatedToAssignments);
                autoAssignCreatedToSelection = EditorGUILayout.ToggleLeft(
                    "Assign created materials back to the selection", autoAssignCreatedToSelection);

                GUILayout.Space(ToolStyles.SpaceM);

                // The button says exactly what it will do, so the count does not have to be
                // discovered from a dialog after the fact.
                string blocker = GetCreateBlocker();
                List<PlannedMaterial> plan = BuildPlan();

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new ToolStyles.DisabledScope(blocker != null || plan.Count == 0))
                    {
                        string label = plan.Count == 1 ? "Create 1 material" : $"Create {plan.Count} materials";
                        if (GUILayout.Button(label, ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonL),
                                GUILayout.Height(ToolStyles.ActionHeight)))
                            CreateMaterials(plan);
                    }

                    GUILayout.FlexibleSpace();

                    // One specific reason, rather than a list of every requirement at once.
                    if (blocker != null)
                        ToolStyles.ColouredLabel(blocker, ToolStyles.Hint, ToolStyles.Warn);
                }
            }
        }

        /// <summary>
        /// The destination folder, as a drop target.
        ///
        /// The same treatment as the AssetBundle Viewer's bundle field: dragging a folder in from
        /// the Project window is how this is set nine times out of ten, so that is the control. Browse
        /// remains for the tenth — and its panel can create a folder, which is how a destination that
        /// does not exist yet still gets named now that there is no path to type into.
        ///
        /// Browse sits in the card header rather than under the zone, matching the AssetBundle
        /// Viewer: the fallback for a drop target belongs beside the card's title, not below the
        /// target where it competes with it.
        /// </summary>
        private void DrawRootFolderField()
        {
            var rect = GUILayoutUtility.GetRect(0, ToolStyles.DropZoneHeight, GUILayout.ExpandWidth(true));

            string normalized = NormalizeAssetsPath(rootFolderPath);

            ToolStyles.DropZone(rect, rootDragHover, normalized);

            const float lineOne = 18f;
            const float lineTwo = 16f;
            var top = rect.y + (rect.height - (lineOne + lineTwo)) / 2f;
            var upper = new Rect(rect.x + 10, top, rect.width - 20, lineOne);
            var lower = new Rect(rect.x + 10, top + lineOne, rect.width - 20, lineTwo);

            if (string.IsNullOrEmpty(normalized))
            {
                GUI.Label(upper, "Drag a destination folder here", ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(lower, "from the Project window, or use Browse", ToolStyles.Centred(ToolStyles.Hint));
            }
            else
            {
                GUI.Label(upper, System.IO.Path.GetFileName(normalized.TrimEnd('/')),
                    ToolStyles.Centred(ToolStyles.CardTitle));
                GUI.Label(lower, ToolStyles.Elide(normalized, ToolStyles.MonoCharsFor(lower.width)),
                    ToolStyles.Centred(ToolStyles.MonoSmall));
            }

            HandleFolderDrop(rect);

            if (!string.IsNullOrEmpty(normalized) && !AssetDatabase.IsValidFolder(normalized))
                ToolStyles.ColouredLabel("Will be created", ToolStyles.Hint, ToolStyles.Warn);
        }

        private void HandleFolderDrop(Rect dropArea)
        {
            Event current = Event.current;

            if (current.type == EventType.DragExited || current.type == EventType.MouseLeaveWindow)
            {
                if (!rootDragHover) return;
                rootDragHover = false;
                Repaint();
                return;
            }

            if (current.type != EventType.DragUpdated && current.type != EventType.DragPerform) return;
            if (!dropArea.Contains(current.mousePosition)) return;

            string[] paths = DragAndDrop.paths;
            bool isFolder = paths != null && paths.Length > 0 && AssetDatabase.IsValidFolder(paths[0]);

            DragAndDrop.visualMode = isFolder ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;

            if (rootDragHover != isFolder)
            {
                rootDragHover = isFolder;
                Repaint();
            }

            if (current.type == EventType.DragPerform && isFolder)
            {
                DragAndDrop.AcceptDrag();
                rootDragHover = false;
                rootFolderPath = paths[0];
                GUI.FocusControl(null);
            }

            current.Use();
        }

        /// <summary>Shows the exact asset paths that will be produced, built by the same code that creates them.</summary>
        /// <summary>Shows the exact asset paths that will be produced, built by the code that creates them.</summary>
        private void DrawPlan()
        {
            if (selectedTmps.Count == 0)
            {
                ToolStyles.ValueBox("", "No TMP objects selected");
                return;
            }

            List<PlannedMaterial> plan = BuildPlan();
            int skipped = selectedTmps.Count - plan.Count;

            string summary = $"{selectedTmps.Count} TMP object(s) selected";
            if (skipped > 0) summary += $", {skipped} without a material";
            GUILayout.Label(summary, ToolStyles.Hint);

            if (plan.Count == 0) return;

            GUILayout.Space(ToolStyles.SpaceXS);

            int shown = Mathf.Min(plan.Count, MaxPreviewRows);
            bool more = plan.Count > shown;
            var rect = GUILayoutUtility.GetRect(0, (shown + (more ? 1 : 0)) * ToolStyles.ListRowHeight + 2f,
                GUILayout.ExpandWidth(true));

            GUI.Box(rect, GUIContent.none, ToolStyles.Inset);

            for (int i = 0; i < shown; i++)
            {
                var row = new Rect(rect.x + 1, rect.y + 1 + i * ToolStyles.ListRowHeight,
                    rect.width - 2, ToolStyles.ListRowHeight);
                if (i % 2 == 1)
                    EditorGUI.DrawRect(row, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));

                var text = new Rect(row.x + 8, row.y, row.width - 16, row.height);
                GUI.Label(text, ToolStyles.Elide(plan[i].AssetPath, ToolStyles.MonoCharsFor(text.width)),
                    ToolStyles.MonoSmall);
            }

            if (!more) return;
            var last = new Rect(rect.x + 9, rect.y + 1 + shown * ToolStyles.ListRowHeight,
                rect.width - 18, ToolStyles.ListRowHeight);
            GUI.Label(last, $"and {plan.Count - shown} more", ToolStyles.Hint);
        }

        private void DrawAssignSection()
        {
            // Every structural change to the list is recorded here and applied after drawing finishes.
            // Adding or removing a row mid-draw leaves the rest of the pass drawing a different number
            // of controls than the layout pass measured.
            bool addRow = false;
            bool clearAll = false;
            bool addSelected = false;
            int removeIndex = -1;
            int readyCount = 0;

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var header = ToolStyles.CardHeader("Assign materials");

                var clearRect = new Rect(header.xMax - ToolStyles.ButtonS, header.y + 1,
                    ToolStyles.ButtonS, ToolStyles.ControlHeight);
                var selectionRect = new Rect(clearRect.x - ToolStyles.SpaceS - ToolStyles.ButtonL,
                    header.y + 1, ToolStyles.ButtonL, ToolStyles.ControlHeight);
                var rowRect = new Rect(selectionRect.x - ToolStyles.SpaceS - ToolStyles.ButtonS,
                    header.y + 1, ToolStyles.ButtonS, ToolStyles.ControlHeight);

                addRow = GUI.Button(rowRect, "Add row", ToolStyles.Secondary);

                using (new ToolStyles.DisabledScope(selectedTmps.Count == 0))
                    addSelected = GUI.Button(selectionRect, $"Add selection ({selectedTmps.Count})",
                        ToolStyles.Secondary);

                using (new ToolStyles.DisabledScope(assignments.Count == 0))
                    clearAll = GUI.Button(clearRect, "Clear", ToolStyles.Secondary);

                GUILayout.Space(ToolStyles.SpaceS);
                GUILayout.Label("Pair TMP objects with material assets and apply them in one go.",
                    ToolStyles.Hint);
                GUILayout.Space(ToolStyles.SpaceM);

                if (assignments.Count == 0)
                {
                    ToolStyles.ValueBox("", "No rows yet — use Add selection");
                }
                else
                {
                    DrawAssignHeader();

                    for (int i = 0; i < assignments.Count; i++)
                    {
                        AssignmentItem item = assignments[i];
                        if (item.IsReady) readyCount++;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            // A status glyph per row, instead of a full-width box that pushed every
                            // following row down whenever one object lacked a TMP component.
                            DrawRowStatusIcon(item);

                            item.tmpGameObject = (GameObject)EditorGUILayout.ObjectField(
                                item.tmpGameObject, typeof(GameObject), true);
                            item.materialToAssign = (Material)EditorGUILayout.ObjectField(
                                item.materialToAssign, typeof(Material), false);

                            var previous = ToolStyles.SecondaryCompact.normal.textColor;
                            ToolStyles.SecondaryCompact.normal.textColor = ToolStyles.Err;
                            if (GUILayout.Button("×", ToolStyles.SecondaryCompact,
                                    GUILayout.Width(RowButtonWidth),
                                    GUILayout.Height(ToolStyles.ControlHeight)))
                                removeIndex = i;
                            ToolStyles.SecondaryCompact.normal.textColor = previous;
                        }
                    }
                }

                GUILayout.Space(ToolStyles.SpaceM);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new ToolStyles.DisabledScope(readyCount == 0))
                    {
                        string label = readyCount == 1 ? "Apply to 1 object" : $"Apply to {readyCount} objects";
                        if (GUILayout.Button(label, ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonL),
                                GUILayout.Height(ToolStyles.ActionHeight)))
                            ApplyMaterials();
                    }
                    GUILayout.FlexibleSpace();
                }
            }

            if (addRow) assignments.Add(new AssignmentItem());
            if (clearAll) assignments.Clear();
            if (addSelected) AddSelectedTmpsToAssignments();
            if (removeIndex >= 0 && removeIndex < assignments.Count) assignments.RemoveAt(removeIndex);
        }

        private static void DrawAssignHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(RowIconWidth);
                GUILayout.Label("TMP OBJECT", ToolStyles.ColumnHeader);
                GUILayout.Label("MATERIAL", ToolStyles.ColumnHeader);
                GUILayout.Space(RowButtonWidth + 4f);
            }
        }

        /// <summary>
        /// Marks only rows that need attention - a ready row draws blank space. Icons that mean "fine" add
        /// visual noise to exactly the rows you don't need to look at.
        /// </summary>
        private static void DrawRowStatusIcon(AssignmentItem item)
        {
            string icon = null;
            string tooltip = null;

            if (item.tmpGameObject == null)
            {
                icon = "console.warnicon.sml";
                tooltip = "No GameObject assigned.";
            }
            else if (item.TMP == null)
            {
                icon = "console.erroricon.sml";
                tooltip = "That GameObject has no TMP_Text component.";
            }
            else if (item.materialToAssign == null)
            {
                icon = "console.warnicon.sml";
                tooltip = "No material assigned.";
            }

            if (icon == null)
            {
                GUILayout.Space(RowIconWidth);
                return;
            }

            GUIContent content = EditorGUIUtility.IconContent(icon);
            GUILayout.Label(new GUIContent(content.image, tooltip), GUILayout.Width(RowIconWidth), GUILayout.Height(16));
        }

        private void DrawStatusBar()
        {
            if (string.IsNullOrEmpty(statusMessage)) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ToolStyles.SpaceL);
                EditorGUILayout.HelpBox(statusMessage, statusType);
                GUILayout.Space(ToolStyles.SpaceL);
            }
            GUILayout.Space(ToolStyles.SpaceM);
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusType = type;
        }

        // ---- Planning ----------------------------------------------------------------------------

        /// <summary>Joins the name segments with underscores, skipping any that are blank.</summary>
        private static string JoinNameParts(params string[] parts)
        {
            var kept = new List<string>();
            foreach (string part in parts)
                if (!string.IsNullOrWhiteSpace(part)) kept.Add(part.Trim());
            return string.Join("_", kept);
        }

        /// <summary>
        /// What a create run would produce. Shared by the preview and the create itself so the two can
        /// never disagree about naming or destination.
        /// </summary>
        private List<PlannedMaterial> BuildPlan()
        {
            var plan = new List<PlannedMaterial>();

            string typeFolder = CombineAssetPath(NormalizeAssetsPath(rootFolderPath), GetTypeFolderName());
            string typeName = GetTypeNameForNaming();

            foreach (TMP_Text tmp in selectedTmps)
            {
                if (!tmp) continue;
                if (GetSourceMaterial(tmp) == null) continue;

                string fontAssetName = tmp.font != null ? tmp.font.name : "NoFontAsset";
                // Joined from the parts that are actually there, so an empty prefix does not leave
                // a double underscore in every name it produces.
                string baseName = SanitizeFileName(JoinNameParts(fontAssetName, namePrefix, typeName));

                plan.Add(new PlannedMaterial(tmp, CombineAssetPath(typeFolder, $"{baseName}.mat")));
            }

            return plan;
        }

        /// <summary>
        /// The material to copy from, or null when there is nothing to copy.
        ///
        /// Deliberately only fontSharedMaterial. The fontMaterial fallback that used to be here ran
        /// in exactly the case it could not survive: it is reached only when the shared material is
        /// null, and TMP's fontMaterial getter answers by instantiating one from the shared material
        /// — new Material(null), which throws. Since this is called from the draw path, the throw
        /// escaped mid-layout and left the scroll view unclosed, which is the GUIClip imbalance that
        /// came with it.
        ///
        /// It is also a getter with a side effect: it creates and assigns an instance material on
        /// the object. Merely looking at the window would have dirtied the scene.
        /// </summary>
        private static Material GetSourceMaterial(TMP_Text tmp) => tmp.fontSharedMaterial;

        /// <summary>The single reason creation is blocked, or null when it can proceed.</summary>
        private string GetCreateBlocker()
        {
            if (string.IsNullOrWhiteSpace(NormalizeAssetsPath(rootFolderPath)))
                return "Root Folder is required, and must be inside Assets/.";

            if (materialType == MaterialType.Other && string.IsNullOrWhiteSpace(customTypeName))
                return "Custom Type is required when Type is Other.";

            if (selectedTmps.Count == 0)
                return "Select one or more GameObjects with TMP_Text components.";

            if (BuildPlan().Count == 0)
                return "None of the selected TMP objects have a material to copy.";

            return null;
        }

        // ---- Actions -----------------------------------------------------------------------------

        private void CreateMaterials(List<PlannedMaterial> plan)
        {
            string typeFolder = CombineAssetPath(NormalizeAssetsPath(rootFolderPath), GetTypeFolderName());

            // Deliberately before the undo group opens: creating a folder is not undoable through
            // the Undo API, so it must not look like part of a group that claims to be.
            if (!EnsureFolderExists(typeFolder))
            {
                SetStatus($"Could not create folder: {typeFolder}", MessageType.Error);
                return;
            }

            int createdCount = 0;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            foreach (PlannedMaterial planned in plan)
            {
                TMP_Text tmp = planned.Source;
                if (!tmp) continue;

                Material source = GetSourceMaterial(tmp);
                if (source == null) continue;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(planned.AssetPath);
                var duplicated = new Material(source) { name = Path.GetFileNameWithoutExtension(assetPath) };

                AssetDatabase.CreateAsset(duplicated, assetPath);

                // Registered so undo takes the asset back out again. Without this the group undid
                // the assignments it made and left every .mat it wrote behind — which is the worse
                // half to leave, because the files are what you then have to find and delete by hand.
                Undo.RegisterCreatedObjectUndo(duplicated, "Create TMP Material");
                createdCount++;

                if (autoAddCreatedToAssignments)
                    AddAssignment(tmp.gameObject, duplicated);

                if (autoAssignCreatedToSelection)
                {
                    // RecordObject already flags the object dirty; SetDirty afterwards was redundant.
                    Undo.RecordObject(tmp, "Assign TMP Material");
                    tmp.fontSharedMaterial = duplicated;
                }
            }

            // No SaveAssets()/Refresh() here: CreateAsset writes and imports each asset immediately, so those
            // only added a project-wide asset save and a full reimport scan.
            Undo.SetCurrentGroupName("Create TMP Materials");
            Undo.CollapseUndoOperations(undoGroup);

            SetStatus($"Created {createdCount} material(s) in {typeFolder}.", MessageType.Info);
            Debug.Log($"[TMP Materials] Created {createdCount} material(s) in {typeFolder}.");

            // The assignment list may have grown mid-pass, which would leave this frame's layout out of step
            // with what was measured. Unwind and let the next pass draw the new state.
            GUIUtility.ExitGUI();
        }

        private void ApplyMaterials()
        {
            int applied = 0;
            int skipped = 0;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            foreach (AssignmentItem item in assignments)
            {
                if (item == null || !item.IsReady)
                {
                    skipped++;
                    continue;
                }

                // RecordObject already flags the object dirty; SetDirty afterwards was redundant.
                Undo.RecordObject(item.TMP, "Apply TMP Material");
                item.TMP.fontSharedMaterial = item.materialToAssign;

                applied++;
            }

            Undo.SetCurrentGroupName("Apply TMP Materials");
            Undo.CollapseUndoOperations(undoGroup);

            string message = $"Applied materials to {applied} TMP object(s).";
            if (skipped > 0) message += $" Skipped {skipped} incomplete row(s).";

            SetStatus(message, skipped > 0 ? MessageType.Warning : MessageType.Info);
            Debug.Log($"[TMP Materials] {message}");
        }

        private void AddSelectedTmpsToAssignments()
        {
            foreach (TMP_Text tmp in selectedTmps)
            {
                if (!tmp) continue;
                AddAssignment(tmp.gameObject, tmp.fontSharedMaterial);
            }
        }

        /// <summary>
        /// Adds a row, or updates the existing row for that GameObject. Without the check, running
        /// "+ Selection" twice produced duplicate rows that then applied the same material twice.
        /// </summary>
        private void AddAssignment(GameObject tmpGameObject, Material material)
        {
            if (!tmpGameObject) return;

            foreach (AssignmentItem existing in assignments)
            {
                if (existing != null && existing.tmpGameObject == tmpGameObject)
                {
                    existing.materialToAssign = material;
                    return;
                }
            }

            assignments.Add(new AssignmentItem
            {
                tmpGameObject = tmpGameObject,
                materialToAssign = material
            });
        }

        private void PickRootFolder()
        {
            string absolute = EditorUtility.OpenFolderPanel("Select Root Folder (must be inside Assets)", Application.dataPath, "");
            if (string.IsNullOrEmpty(absolute)) return;

            absolute = absolute.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');

            if (!absolute.StartsWith(dataPath, StringComparison.Ordinal))
            {
                SetStatus("That folder is outside this project's Assets folder.", MessageType.Error);
                return;
            }

            rootFolderPath = "Assets" + absolute.Substring(dataPath.Length);
            GUI.FocusControl(null);
        }

        // ---- Paths -------------------------------------------------------------------------------

        private string GetTypeFolderName() =>
            materialType == MaterialType.Other ? SanitizeFolderName(customTypeName) : materialType.ToString();

        private string GetTypeNameForNaming() =>
            materialType == MaterialType.Other ? customTypeName.Trim() : materialType.ToString();

        private static string NormalizeAssetsPath(string path)
        {
            path = (path ?? "").Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path)) return "";

            if (!path.StartsWith("Assets", StringComparison.Ordinal))
                path = "Assets/" + path.TrimStart('/');

            return path.TrimEnd('/');
        }

        private static string CombineAssetPath(string left, string right)
        {
            left = (left ?? "").Replace('\\', '/').TrimEnd('/');
            right = (right ?? "").Replace('\\', '/').TrimStart('/');
            return $"{left}/{right}";
        }

        private static bool EnsureFolderExists(string assetFolderPath)
        {
            assetFolderPath = (assetFolderPath ?? "").Replace('\\', '/');

            if (AssetDatabase.IsValidFolder(assetFolderPath))
                return true;

            string[] parts = assetFolderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "Assets") return false;

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = CombineAssetPath(current, parts[i]);
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);

                current = next;
            }

            return AssetDatabase.IsValidFolder(assetFolderPath);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unnamed";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            // Collapse runs of whitespace properly; a single Replace("  ", " ") leaves "a    b" as "a  b".
            return string.Join(" ", name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string SanitizeFolderName(string name)
        {
            name = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) return "Other";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Trim();
        }
    }
}
