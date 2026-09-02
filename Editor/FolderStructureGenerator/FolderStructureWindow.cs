using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Generating folder structures from reusable profiles.
    ///
    /// Settings live as JSON in ProjectSettings/ (see FolderStructureSettings), loaded via the
    /// singleton. Edits flush to disk on focus-loss or close; structural changes save immediately.
    ///
    /// Drawn with <see cref="ToolStyles"/>, so it follows the same rules as the rest of the toolset.
    /// </summary>
    public class FolderStructureWindow : EditorWindow
    {
        private FolderStructureSettings settings;
        private int selectedProfileIndex;
        private string variableValue = "";
        private Vector2 scrollPosition;
        private Vector2 previewScrollPosition;

        // Deliberately not called "hasUnsavedChanges": that name shadows EditorWindow.hasUnsavedChanges,
        // which drives Unity's own save-on-close prompt. This window saves silently instead.
        private bool hasPendingSave;

        /// <summary>Tallest the preview grows before it scrolls internally.</summary>
        private const float MaxPreviewHeight = 160f;

        private static readonly Vector2 MinWindowSize = new Vector2(430, 460);

        [MenuItem("Utilities/Folder Structure Generator", false, 1001)]
        public static void ShowWindow()
        {
            GetWindow<FolderStructureWindow>("Folder Structure Generator").minSize = MinWindowSize;
        }

        private void OnEnable()
        {
            settings = FolderStructureSettings.Instance;

            // Without this, hover states only repaint when something else happens to trigger a frame.
            wantsMouseMove = true;
        }

        private void OnDisable() => FlushPendingSave();
        private void OnLostFocus() => FlushPendingSave();

        private void FlushPendingSave()
        {
            if (!hasPendingSave || settings == null) return;
            settings.Save();
            hasPendingSave = false;
        }

        private FolderProfile SelectedProfile =>
            settings.profiles.Count > 0 && selectedProfileIndex >= 0 && selectedProfileIndex < settings.profiles.Count
                ? settings.profiles[selectedProfileIndex]
                : null;

        // ---------- drawing ----------

        private void OnGUI()
        {
            if (settings == null) return;

            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            EditorGUIUtility.labelWidth = 110;

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            GUILayout.Space(ToolStyles.SpaceL);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawCreateCard();
                    GUILayout.Space(ToolStyles.SpaceL);
                    DrawProfilesCard();
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }

            GUILayout.Space(ToolStyles.SpaceL);
            EditorGUILayout.EndScrollView();

            if (GUI.changed) hasPendingSave = true;
        }

        private void DrawCreateCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                selectedProfileIndex = Mathf.Clamp(selectedProfileIndex, 0,
                    Mathf.Max(0, settings.profiles.Count - 1));
                var selected = SelectedProfile;

                ToolStyles.CardHeader("Create");
                GUILayout.Space(ToolStyles.SpaceM);

                var names = new List<string>();
                foreach (var profile in settings.profiles) names.Add(profile.profileName);
                if (names.Count == 0) names.Add("No profiles");

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Profile", ToolStyles.RowLabel, GUILayout.Width(ToolStyles.FormLabelWidth));
                    selectedProfileIndex = EditorGUILayout.Popup(selectedProfileIndex, names.ToArray(),
                        GUILayout.Height(ToolStyles.ControlHeight));
                }

                // The variable value leads: it is the one thing typed on every run.
                if (selected != null)
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var label = string.IsNullOrWhiteSpace(selected.variableName)
                            ? "Value"
                            : char.ToUpper(selected.variableName[0]) + selected.variableName.Substring(1);
                        GUILayout.Label(label, ToolStyles.RowLabel, GUILayout.Width(ToolStyles.FormLabelWidth));
                        variableValue = EditorGUILayout.TextField(variableValue,
                            GUILayout.Height(ToolStyles.ControlHeight));
                    }
                }

                GUILayout.Space(ToolStyles.SpaceM);
                DrawPreview(selected);
                GUILayout.Space(ToolStyles.SpaceM);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new ToolStyles.DisabledScope(selected == null))
                    {
                        if (GUILayout.Button("Create folders", ToolStyles.Primary,
                                GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ActionHeight)))
                            CreateFromSelectedProfile();
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawPreview(FolderProfile selected)
        {
            if (selected == null)
            {
                EditorGUILayout.HelpBox("Add a profile below to get started.", MessageType.Info);
                return;
            }

            var paths = FolderStructureEngine.Resolve(selected, variableValue);
            if (paths.Count == 0)
            {
                EditorGUILayout.HelpBox("This profile has no folder entries yet.", MessageType.Info);
                return;
            }

            var rowHeight = ToolStyles.ListRowHeight;
            var height = Mathf.Min(MaxPreviewHeight, paths.Count * rowHeight + 2f);
            var rect = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));

            GUI.Box(rect, GUIContent.none, ToolStyles.Inset);
            var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
            var content = new Rect(0, 0,
                ToolStyles.ListContentWidth(inner, paths.Count, rowHeight),
                paths.Count * rowHeight);

            previewScrollPosition = GUI.BeginScrollView(inner, previewScrollPosition, content);

            for (var i = 0; i < paths.Count; i++)
            {
                var row = new Rect(0, i * rowHeight, content.width, rowHeight);
                if (i % 2 == 1)
                    EditorGUI.DrawRect(row, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));

                var navWidth = paths[i].addToQuickNav ? 42f : 0f;
                var pathRect = new Rect(row.x + 8, row.y, Mathf.Max(40f, row.width - 16 - navWidth), row.height);
                GUI.Label(pathRect, ToolStyles.Elide(paths[i].displayPath,
                    ToolStyles.MonoCharsFor(pathRect.width)), ToolStyles.MonoSmall);

                if (navWidth <= 0f) continue;

                // Marked in the accent rather than with an inline colour tag: rich text in a label is
                // a second way of saying "this is highlighted" that the palette cannot follow.
                ToolStyles.ColouredLabel(new Rect(pathRect.xMax, row.y, navWidth, row.height), "Nav",
                    ToolStyles.StatusText, ToolStyles.Accent);
            }

            GUI.EndScrollView();
        }

        private void CreateFromSelectedProfile()
        {
            FolderProfile profile = SelectedProfile;
            if (profile == null) return;

            bool needsValue = profile.folders.Exists(f => f != null && !string.IsNullOrEmpty(f.pathTemplate) && f.pathTemplate.Contains(profile.Token));
            if (needsValue && string.IsNullOrWhiteSpace(FolderStructureEngine.SanitizeValue(variableValue)))
            {
                EditorUtility.DisplayDialog("Folder Structure",
                    $"Please enter a value for \"{profile.variableName}\" before creating.", "OK");
                return;
            }

            FolderStructureEngine.CreateResult result = FolderStructureEngine.Create(profile, variableValue);

            string quickNavNote = "";
            if (result.quickNavRequested)
            {
                quickNavNote = result.quickNavAvailable
                    ? $" Added {result.quickNavAdded} folder(s) to QuickNav tab '{FolderStructureEngine.ResolveTabName(profile, variableValue)}'."
                    : " QuickNav not found — skipped favorites.";
            }

            if (result.errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Folder Structure",
                    $"Created {result.created}, skipped {result.skipped}.{quickNavNote}\n\nErrors:\n- {string.Join("\n- ", result.errors)}", "OK");
            }
            else
            {
                Debug.Log($"[Folder Structure] '{profile.profileName}': created {result.created} folder(s), skipped {result.skipped} existing.{quickNavNote}");
            }
        }

        private void DrawProfilesCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var header = ToolStyles.CardHeader("Profiles");

                var addRect = new Rect(header.xMax - ToolStyles.ButtonL, header.y + 1,
                    ToolStyles.ButtonL, ToolStyles.ControlHeight);
                if (GUI.Button(addRect, "Add profile", ToolStyles.Secondary))
                {
                    settings.profiles.Add(new FolderProfile { profileName = "New Profile", isExpanded = true });
                    settings.Save();
                    // Adding a control mid-pass changes the count between Layout and Repaint;
                    // ExitGUI abandons the pass so the next one draws the new list cleanly.
                    GUIUtility.ExitGUI();
                }

                GUILayout.Space(ToolStyles.SpaceM);

                if (settings.profiles.Count == 0)
                {
                    ToolStyles.ValueBox("", "No profiles yet");
                    return;
                }

                var toRemove = -1;
                var toDuplicate = -1;

                for (var i = 0; i < settings.profiles.Count; i++)
                {
                    if (DrawProfile(settings.profiles[i], ref toRemove, ref toDuplicate, i))
                        GUILayout.Space(ToolStyles.SpaceM);
                }

                if (toDuplicate != -1)
                {
                    var copy = CloneProfile(settings.profiles[toDuplicate]);
                    copy.profileName = GetUniqueProfileName(copy.profileName);
                    copy.isExpanded = true;
                    settings.profiles.Insert(toDuplicate + 1, copy);
                    settings.Save();
                    GUIUtility.ExitGUI();
                }

                if (toRemove == -1) return;
                settings.profiles.RemoveAt(toRemove);
                settings.Save();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>One profile, collapsed to a summary row or expanded for editing.</summary>
        private bool DrawProfile(FolderProfile profile, ref int toRemove, ref int toDuplicate, int index)
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Inset))
            {
                if (!profile.isExpanded)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(profile.profileName, ToolStyles.CardTitle);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(profile.folders.Count + (profile.folders.Count == 1 ? " folder  ·  " : " folders  ·  ")
                                        + profile.Token, ToolStyles.StatusText);
                        GUILayout.Space(ToolStyles.SpaceM);
                        if (GUILayout.Button("Edit", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                                GUILayout.Height(ToolStyles.ControlHeight)))
                        {
                            profile.isExpanded = true;
                            GUIUtility.ExitGUI();
                        }
                    }
                    return true;
                }

                profile.profileName = EditorGUILayout.TextField("Profile name", profile.profileName);
                profile.variableName = EditorGUILayout.TextField(new GUIContent("Variable name",
                    "Its token is the name in braces, used in the paths below."), profile.variableName);
                GUILayout.Label("Use " + profile.Token + " in the paths below.", ToolStyles.Hint);

                GUILayout.Space(ToolStyles.SpaceM);
                GUILayout.Label("FOLDERS", ToolStyles.ColumnHeader);
                GUILayout.Space(ToolStyles.SpaceS);

                DrawFolderEntries(profile);

                if (profile.folders.Exists(f => f != null && f.addToQuickNav)) DrawQuickNav(profile);

                GUILayout.Space(ToolStyles.SpaceM);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Done", ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                    {
                        profile.isExpanded = false;
                        GUIUtility.ExitGUI();
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Duplicate", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        toDuplicate = index;

                    GUILayout.Space(ToolStyles.SpaceS);

                    // Colour rather than a distinct style: it is an ordinary button that happens to
                    // destroy something, and inventing a "danger button" would be a third level.
                    var previousDelete = ToolStyles.Secondary.normal.textColor;
                    ToolStyles.Secondary.normal.textColor = ToolStyles.Err;
                    if (GUILayout.Button("Delete", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        toRemove = index;
                    ToolStyles.Secondary.normal.textColor = previousDelete;
                }
            }
            return true;
        }

        private void DrawFolderEntries(FolderProfile profile)
        {
            var entryToRemove = -1;

            for (var j = 0; j < profile.folders.Count; j++)
            {
                var entry = profile.folders[j];

                using (new EditorGUILayout.HorizontalScope())
                {
                    entry.root = (PathRoot)EditorGUILayout.EnumPopup(entry.root, GUILayout.Width(92),
                        GUILayout.Height(ToolStyles.ControlHeight));
                    entry.pathTemplate = EditorGUILayout.TextField(entry.pathTemplate,
                        GUILayout.Height(ToolStyles.ControlHeight));

                    var nav = GUILayout.Toggle(entry.addToQuickNav, new GUIContent("Nav",
                            "Add this folder to the QuickNav favourites tab after creation."),
                        ToolStyles.SecondaryCompact, GUILayout.Width(38),
                        GUILayout.Height(ToolStyles.ControlHeight));
                    if (nav != entry.addToQuickNav) entry.addToQuickNav = nav;

                    var previousRemove = ToolStyles.Secondary.normal.textColor;
                    ToolStyles.Secondary.normal.textColor = ToolStyles.Err;
                    if (GUILayout.Button("×", ToolStyles.Secondary, GUILayout.Width(ToolStyles.IconWidth),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                        entryToRemove = j;
                    ToolStyles.Secondary.normal.textColor = previousRemove;
                }

                if (entry.root != PathRoot.Custom) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(96);
                    entry.customRoot = EditorGUILayout.TextField(entry.customRoot,
                        GUILayout.Height(ToolStyles.ControlHeight));
                    if (GUILayout.Button("…", ToolStyles.Secondary, GUILayout.Width(ToolStyles.IconWidth),
                            GUILayout.Height(ToolStyles.ControlHeight)))
                    {
                        var picked = EditorUtility.OpenFolderPanel("Select custom base folder",
                            entry.customRoot, "");
                        if (!string.IsNullOrEmpty(picked)) entry.customRoot = picked;
                    }
                    GUILayout.Space(ToolStyles.IconWidth + 4f);
                }
            }

            if (entryToRemove != -1)
            {
                profile.folders.RemoveAt(entryToRemove);
                settings.Save();
                GUIUtility.ExitGUI();
            }

            GUILayout.Space(ToolStyles.SpaceS);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add folder", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonM),
                        GUILayout.Height(ToolStyles.ControlHeight)))
                {
                    profile.folders.Add(new FolderEntry { root = PathRoot.Assets, pathTemplate = "" });
                    settings.Save();
                    GUIUtility.ExitGUI();
                }
                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawQuickNav(FolderProfile profile)
        {
            GUILayout.Space(ToolStyles.SpaceM);
            GUILayout.Label("QUICKNAV", ToolStyles.ColumnHeader);
            GUILayout.Space(ToolStyles.SpaceS);

            profile.quickNavTabName = EditorGUILayout.TextField(new GUIContent("Tab name",
                    "Tab that Nav-flagged folders are added to. Supports the variable token; blank "
                    + "uses the profile name."), profile.quickNavTabName);
            profile.quickNavTabColor = EditorGUILayout.ColorField("Tab colour", profile.quickNavTabColor);

            if (!QuickNavBridge.IsAvailable)
                GUILayout.Label("QuickNavigation is not in this project. The folders are still created; "
                    + "the tab is only added when QuickNav is present.", ToolStyles.Hint);
        }

        /// <summary>Deep-copies a profile so the duplicate's folder list is independent of the original.</summary>
        private static FolderProfile CloneProfile(FolderProfile source)
        {
            var clone = new FolderProfile
            {
                profileName = source.profileName,
                variableName = source.variableName,
                quickNavTabName = source.quickNavTabName,
                quickNavTabColor = source.quickNavTabColor,
                isExpanded = source.isExpanded,
                folders = new List<FolderEntry>()
            };

            foreach (FolderEntry entry in source.folders)
            {
                clone.folders.Add(new FolderEntry
                {
                    root = entry.root,
                    customRoot = entry.customRoot,
                    pathTemplate = entry.pathTemplate,
                    addToQuickNav = entry.addToQuickNav
                });
            }

            return clone;
        }

        /// <summary>Appends " Copy" (and a counter if needed) so the duplicate has a distinct name.</summary>
        private string GetUniqueProfileName(string baseName)
        {
            string trimmed = string.IsNullOrWhiteSpace(baseName) ? "Profile" : baseName.Trim();
            string candidate = trimmed + " Copy";
            int counter = 2;

            while (settings.profiles.Exists(p => p.profileName == candidate))
            {
                candidate = $"{trimmed} Copy {counter}";
                counter++;
            }

            return candidate;
        }

    }
}
