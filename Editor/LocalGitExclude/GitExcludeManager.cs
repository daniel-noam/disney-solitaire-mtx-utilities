using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;

namespace Utilities.Editor
{
    /// <summary>Editor window for managing the local Git exclude file (.git/info/exclude).</summary>
    public class GitExcludeManager : EditorWindow
    {
        /// <summary>
        /// The two mechanisms for keeping local things out of git. They apply to opposite kinds of file,
        /// so they get separate tabs rather than one merged list.
        /// </summary>
        private enum Tab
        {
            Exclude,
            SkipWorktree,
        }

        private static readonly string[] TabLabels = { "Exclude", "Skip Worktree" };

        private Tab activeTab = Tab.Exclude;

        private Vector2 scrollPosition;
        private string fileContents = "";

        private Vector2 skipScrollPosition;
        private List<string> skippedPaths = new List<string>();
        private List<GitSkipWorktreePauseStore.PausedEntry> pausedEntries = new List<GitSkipWorktreePauseStore.PausedEntry>();
        private bool skippedPathsLoaded;

        // Resolved on enable/focus rather than per repaint, since locating the repository touches the
        // filesystem on the way up.
        private GitRepositoryInfo repository;

        [MenuItem("Utilities/Git Local Exclude Manager", false, 1005)]
        public static void ShowWindow()
        {
            var window = GetWindow<GitExcludeManager>("Git Local Exclude Manager");
            window.minSize = new Vector2(300, 380);
        }

        private void OnEnable()
        {
            // Without this, hover states only repaint when something else triggers a frame.
            wantsMouseMove = true;
            RefreshFileContents();
        }
        private void OnFocus() => RefreshFileContents();
        private void OnSelectionChange() => Repaint();

        // ---- GUI ---------------------------------------------------------------------------------

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);
            if (Event.current.type == EventType.MouseMove) Repaint();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (var i = 0; i < TabLabels.Length; i++)
                {
                    if (GUILayout.Toggle(activeTab == (Tab)i, TabLabels[i], EditorStyles.toolbarButton,
                            GUILayout.Width(ToolStyles.ButtonL)))
                        activeTab = (Tab)i;
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(ToolStyles.SpaceL);

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
                {
                    if (!repository.IsValid)
                    {
                        EditorGUILayout.HelpBox(
                            "No Git repository found in this project folder or any folder above it.",
                            MessageType.Warning);
                    }
                    else if (activeTab == Tab.Exclude)
                    {
                        DrawExcludeTab();
                    }
                    else
                    {
                        DrawSkipWorktreeTab();
                    }
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }

            GUILayout.Space(ToolStyles.SpaceL);
        }

        /// <summary>
        /// Entries are written relative to the repository root, so a project nested inside a larger
        /// repository has to say so — otherwise the listed paths look wrong beside the Project window.
        /// </summary>
        private void DrawRepositoryNote()
        {
            if (!repository.IsNested) return;

            GUILayout.Label("Repository root  " + ToolStyles.Elide(repository.RepositoryRoot, 60),
                ToolStyles.Hint);
            GUILayout.Label("Paths are prefixed with " + repository.ProjectPrefix + "/", ToolStyles.Hint);
            GUILayout.Space(ToolStyles.SpaceM);
        }

        // ---- Exclude tab -------------------------------------------------------------------------

        private void DrawExcludeTab()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var header = ToolStyles.CardHeader("Local exclude");

                var openRect = new Rect(header.xMax - ToolStyles.ButtonL, header.y + 1,
                    ToolStyles.ButtonL, ToolStyles.ControlHeight);
                if (GUI.Button(openRect, "Open exclude file", ToolStyles.Secondary))
                    OpenExcludeFile(repository.ExcludeFilePath);

                GUILayout.Space(ToolStyles.SpaceM);
                DrawRepositoryNote();

                GUILayout.Label("For files Git does not track. Adds them to .git/info/exclude so they are "
                    + "ignored on this machine only, without touching the shared .gitignore.",
                    ToolStyles.Hint);

                GUILayout.Space(ToolStyles.SpaceM);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Register & check tool paths",
                                "Adds this toolset and the files it writes to the exclude file, then asks "
                                + "Git whether each entry actually takes effect and reports what it found."),
                            ToolStyles.Primary, GUILayout.Width(ToolStyles.ButtonL + ToolStyles.ButtonM),
                            GUILayout.Height(ToolStyles.ActionHeight)))
                    {
                        GitExcludeReminder.Run(true);
                        RefreshFileContents();
                    }
                    GUILayout.FlexibleSpace();
                }

                GUILayout.Space(ToolStyles.SpaceS);

                using (var changeCheck = new EditorGUI.ChangeCheckScope())
                {
                    var autoRegister = EditorGUILayout.ToggleLeft(
                        new GUIContent("Also register quietly on editor load",
                            "Adds any missing entries once per session without reporting anything. "
                            + "Findings it cannot fix are only reported by the button above."),
                        GitExcludeReminder.AutoRegisterEnabled);

                    if (changeCheck.changed) GitExcludeReminder.AutoRegisterEnabled = autoRegister;
                }
            }

            GUILayout.Space(ToolStyles.SpaceL);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card, GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Excluded paths", ToolStyles.CardTitle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("drag assets here to add", ToolStyles.StatusText);
                }

                GUILayout.Space(ToolStyles.SpaceM);

                var rect = GUILayoutUtility.GetRect(0, 80,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                DrawExcludeContents(rect);
            }

            HandleDragAndDrop();
        }

        private void DrawExcludeContents(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, ToolStyles.Inset);

            if (string.IsNullOrWhiteSpace(fileContents))
            {
                GUI.Label(rect, "Nothing excluded yet.", ToolStyles.Centred(ToolStyles.Placeholder));
                return;
            }

            var lines = fileContents.Replace("\r", "").Split('\n');
            var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
            var content = new Rect(0, 0,
                ToolStyles.ListContentWidth(inner, lines.Length, ToolStyles.ListRowHeight),
                lines.Length * ToolStyles.ListRowHeight);

            scrollPosition = GUI.BeginScrollView(inner, scrollPosition, content);

            var first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / ToolStyles.ListRowHeight));
            var last = Mathf.Min(lines.Length,
                first + Mathf.CeilToInt(inner.height / ToolStyles.ListRowHeight) + 1);

            string toRemove = null;

            for (var i = first; i < last; i++)
            {
                var row = new Rect(0, i * ToolStyles.ListRowHeight, content.width, ToolStyles.ListRowHeight);
                if (i % 2 == 1)
                    EditorGUI.DrawRect(row, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));

                var line = lines[i];
                var comment = line.TrimStart().StartsWith("#");
                var blank = string.IsNullOrWhiteSpace(line);

                // Comments are the file's own headings, and blanks are its spacing. Neither is an
                // entry, so neither gets a remove button.
                var removable = !comment && !blank;
                var text = new Rect(row.x + 8, row.y,
                    row.width - 16 - (removable ? ToolStyles.IconWidth : 0f), row.height);

                if (comment)
                    ToolStyles.ColouredLabel(text, line, ToolStyles.MonoSmall, ToolStyles.Faint);
                else
                    GUI.Label(text, ToolStyles.Elide(line, ToolStyles.MonoCharsFor(text.width)),
                        ToolStyles.MonoSmall);

                if (!removable) continue;
                if (RemoveButton(new Rect(row.xMax - ToolStyles.IconWidth - 4f, row.y + 2,
                        ToolStyles.IconWidth, ToolStyles.InRowHeight), "Remove this entry"))
                    toRemove = line;
            }

            GUI.EndScrollView();

            if (toRemove == null) return;
            RemoveExcludeLine(toRemove);
            RefreshFileContents();
            GUIUtility.ExitGUI();
        }

        /// <summary>A small destructive button, coloured rather than given a style of its own.</summary>
        private static bool RemoveButton(Rect rect, string tooltip)
        {
            var previous = ToolStyles.SecondaryCompact.normal.textColor;
            ToolStyles.SecondaryCompact.normal.textColor = ToolStyles.Err;
            var pressed = GUI.Button(rect, new GUIContent("×", tooltip), ToolStyles.SecondaryCompact);
            ToolStyles.SecondaryCompact.normal.textColor = previous;
            return pressed;
        }

        /// <summary>
        /// Drops one exact line from the exclude file, plus the .meta that was added alongside it.
        /// Removing an asset's path but leaving its .meta behind would only half-undo the entry.
        /// </summary>
        private static void RemoveExcludeLine(string line)
        {
            GitRepositoryInfo repository = GitRepositoryInfo.Locate();
            if (!repository.IsValid) return;

            string excludeFilePath = repository.ExcludeFilePath;
            if (!File.Exists(excludeFilePath)) return;

            var lines = new List<string>(File.ReadAllLines(excludeFilePath));
            var companion = line.EndsWith(".meta") ? null : line + ".meta";

            // RemoveAll, not Remove: a hand-edited file can hold the same entry twice.
            var removed = lines.RemoveAll(existing => existing == line);
            if (companion != null) removed += lines.RemoveAll(existing => existing == companion);
            if (removed == 0) return;

            File.WriteAllLines(excludeFilePath, lines);
            Debug.Log($"<b>[Git Exclude]</b> Removed '{line}' from the local exclude file.");
        }

        // ---- Skip-worktree tab -------------------------------------------------------------------

        private void DrawSkipWorktreeTab()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                ToolStyles.CardHeader("Skip worktree");
                GUILayout.Space(ToolStyles.SpaceM);
                DrawRepositoryNote();

                GUILayout.Label("For files Git does track, where you want to keep a local edit. Git stops "
                    + "reporting them as modified and stops overwriting them when you switch branches — "
                    + "a personal package added to Packages/manifest.json, say.", ToolStyles.Hint);

                GUILayout.Space(ToolStyles.SpaceS);
                EditorGUILayout.HelpBox("You also stop receiving real upstream changes to these files. To "
                    + "sync one: stop skipping it, pull, merge your edit back in, then skip it again.",
                    MessageType.Warning);

                if (!GitCommandRunner.IsGitAvailable)
                {
                    EditorGUILayout.HelpBox("The git command line tool was not found, so skip-worktree "
                        + "cannot be managed from here.", MessageType.Error);
                    return;
                }

                GUILayout.Space(ToolStyles.SpaceM);

                var selectionCount = CountSelectedAssets();
                using (new ToolStyles.DisabledScope(selectionCount == 0))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Skip selection (" + selectionCount + ")", ToolStyles.Primary,
                            GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ActionHeight)))
                        ApplySkipWorktreeToSelection(true);

                    GUILayout.Space(ToolStyles.SpaceS);

                    if (GUILayout.Button("Stop skipping selection (" + selectionCount + ")",
                            ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonL + ToolStyles.SpaceXL),
                            GUILayout.Height(ToolStyles.ActionHeight)))
                        ApplySkipWorktreeToSelection(false);

                    GUILayout.FlexibleSpace();
                }
            }

            if (!GitCommandRunner.IsGitAvailable) return;

            // Loaded on Layout only: reading git mid-pass would change the row count between Layout
            // and Repaint.
            if (!skippedPathsLoaded && Event.current.type == EventType.Layout) RefreshSkippedPaths();

            GUILayout.Space(ToolStyles.SpaceL);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card, GUILayout.ExpandHeight(true)))
            {
                var header = ToolStyles.CardHeader("Skipped");

                var refreshRect = new Rect(header.xMax - ToolStyles.ButtonM, header.y + 1,
                    ToolStyles.ButtonM, ToolStyles.ControlHeight);
                if (GUI.Button(refreshRect, "Refresh", ToolStyles.Secondary)) RefreshSkippedPaths();

                if (pausedEntries.Count > 0)
                {
                    GUILayout.Label("Paused files are unprotected — Git can overwrite them until resumed.",
                        ToolStyles.Hint);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Resume all", ToolStyles.Secondary,
                                GUILayout.Width(ToolStyles.ButtonM), GUILayout.Height(ToolStyles.ControlHeight)))
                        {
                            var all = new List<string>();
                            foreach (var entry in pausedEntries) all.Add(entry.Path);
                            ResumePaths(all);
                            GUIUtility.ExitGUI();
                        }
                        GUILayout.FlexibleSpace();
                    }
                }

                GUILayout.Space(ToolStyles.SpaceM);

                DrawSkipList(GUILayoutUtility.GetRect(0, 80,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true)));
            }
        }

        /// <summary>A section heading, a skipped path, or a paused one.</summary>
        private class SkipRow
        {
            public string Header;
            public string Path;
            public bool Paused;
        }

        private List<SkipRow> BuildSkipRows()
        {
            var rows = new List<SkipRow>();
            foreach (var path in skippedPaths) rows.Add(new SkipRow { Path = path });

            if (pausedEntries.Count == 0) return rows;

            rows.Add(new SkipRow { Header = "PAUSED (" + pausedEntries.Count + ")" });
            foreach (var entry in pausedEntries) rows.Add(new SkipRow { Path = entry.Path, Paused = true });
            return rows;
        }

        /// <summary>
        /// The same list treatment as the exclude tab: one inset panel, alternating rows, monospace
        /// paths. They are two views of the same idea and should not look like two different tools.
        /// </summary>
        private void DrawSkipList(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, ToolStyles.Inset);

            var rows = BuildSkipRows();
            if (rows.Count == 0)
            {
                GUI.Label(rect, "No files are marked skip-worktree.",
                    ToolStyles.Centred(ToolStyles.Placeholder));
                return;
            }

            var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
            var content = new Rect(0, 0,
                ToolStyles.ListContentWidth(inner, rows.Count, ToolStyles.ListRowHeight),
                rows.Count * ToolStyles.ListRowHeight);

            skipScrollPosition = GUI.BeginScrollView(inner, skipScrollPosition, content);

            var first = Mathf.Max(0, Mathf.FloorToInt(skipScrollPosition.y / ToolStyles.ListRowHeight));
            var last = Mathf.Min(rows.Count,
                first + Mathf.CeilToInt(inner.height / ToolStyles.ListRowHeight) + 1);

            string toPause = null;
            string toClear = null;
            string toResume = null;

            for (var i = first; i < last; i++)
            {
                var row = new Rect(0, i * ToolStyles.ListRowHeight, content.width, ToolStyles.ListRowHeight);
                var entry = rows[i];

                if (entry.Header != null)
                {
                    EditorGUI.DrawRect(row, ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.CardBg, 0.85f));
                    ToolStyles.ColouredLabel(new Rect(row.x + 8, row.y, row.width - 16, row.height),
                        entry.Header, ToolStyles.ColumnHeader, ToolStyles.Warn);
                    continue;
                }

                if (i % 2 == 1)
                    EditorGUI.DrawRect(row, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));

                var actions = entry.Paused ? ToolStyles.ButtonS + 8f
                    : ToolStyles.ButtonS + ToolStyles.IconWidth + 12f;
                var text = new Rect(row.x + 8, row.y, Mathf.Max(40f, row.width - 16 - actions), row.height);

                if (entry.Paused)
                    ToolStyles.ColouredLabel(text,
                        ToolStyles.Elide(entry.Path, ToolStyles.MonoCharsFor(text.width)),
                        ToolStyles.MonoSmall, ToolStyles.Warn);
                else
                    GUI.Label(text, ToolStyles.Elide(entry.Path, ToolStyles.MonoCharsFor(text.width)),
                        ToolStyles.MonoSmall);

                if (entry.Paused)
                {
                    if (GUI.Button(new Rect(row.xMax - ToolStyles.ButtonS - 4f, row.y + 2,
                                ToolStyles.ButtonS, ToolStyles.InRowHeight),
                            new GUIContent("Resume", "Re-apply skip-worktree to this file."),
                            ToolStyles.SecondaryCompact))
                        toResume = entry.Path;
                    continue;
                }

                if (GUI.Button(new Rect(row.xMax - ToolStyles.ButtonS - ToolStyles.IconWidth - 8f,
                            row.y + 2, ToolStyles.ButtonS, ToolStyles.InRowHeight),
                        new GUIContent("Pause",
                            "Temporarily let Git update this file, then resume to pin it again."),
                        ToolStyles.SecondaryCompact))
                    toPause = entry.Path;

                if (RemoveButton(new Rect(row.xMax - ToolStyles.IconWidth - 4f, row.y + 2,
                        ToolStyles.IconWidth, ToolStyles.InRowHeight), "Stop skipping entirely (forgets it)."))
                    toClear = entry.Path;
            }

            GUI.EndScrollView();

            if (toPause != null)
            {
                if (GitSkipWorktree.Pause(repository, new List<string> { toPause }) > 0)
                    Debug.Log($"<b>[Git Exclude]</b> Paused '{toPause}'. Git can update it now — resume when done.");
                RefreshSkippedPaths();
                GUIUtility.ExitGUI();
            }
            else if (toClear != null)
            {
                GitSkipWorktree.Set(repository, new List<string> { toClear }, false);
                Debug.Log($"<b>[Git Exclude]</b> Stopped skipping '{toClear}'. Git may overwrite it on the "
                          + "next checkout.");
                RefreshSkippedPaths();
                GUIUtility.ExitGUI();
            }
            else if (toResume != null)
            {
                ResumePaths(new List<string> { toResume });
                GUIUtility.ExitGUI();
            }
        }

        private void ResumePaths(List<string> repositoryPaths)
        {
            int resumed = GitSkipWorktree.Resume(repository, repositoryPaths);
            if (resumed > 0)
                Debug.Log($"<b>[Git Exclude]</b> Resumed skip-worktree on {resumed} file(s).");

            RefreshSkippedPaths();
        }

        private void ApplySkipWorktreeToSelection(bool skip)
        {
            int changed = SetSkipWorktreeForSelection(repository, skip);
            if (changed > 0) RefreshSkippedPaths();
        }

        private void RefreshSkippedPaths()
        {
            skippedPaths = GitSkipWorktree.List(repository);
            pausedEntries = GitSkipWorktree.LoadPaused(repository, new HashSet<string>(skippedPaths));
            skippedPathsLoaded = true;
            Repaint();
        }

        private void HandleDragAndDrop()
        {
            Rect dropArea = GUILayoutUtility.GetLastRect();
            Event currentEvent = Event.current;

            if (!dropArea.Contains(currentEvent.mousePosition)) return;

            if (currentEvent.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddPathsToExclude(DragAndDrop.paths);
                currentEvent.Use();
            }
        }

        private void RefreshFileContents()
        {
            repository = GitRepositoryInfo.Locate();

            string path = repository.IsValid ? repository.ExcludeFilePath : null;
            fileContents = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllText(path) : "";
            Repaint();
        }

        private void OpenExcludeFile(string path)
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "");
                RefreshFileContents();
            }

            // 'exclude' has no file extension, so the OS has nothing to associate it with. Force a text
            // editor where we can, and fall back to the default handler if that fails.
            try
            {
#if UNITY_EDITOR_OSX
                System.Diagnostics.Process.Start("open", $"-t \"{path}\"");
#elif UNITY_EDITOR_WIN
                System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\"");
#else
                EditorUtility.OpenWithDefaultApp(path);
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"<b>[Git Exclude]</b> Could not open '{path}' in a text editor ({e.Message}); " +
                                 "falling back to the default application.");
                EditorUtility.OpenWithDefaultApp(path);
            }
        }

        /// <summary>Internal rather than private: GitExcludeReminder registers tool paths through it.</summary>
        internal static int AddPathsToExclude(IEnumerable<string> assetPaths) =>
            ModifyExclude(assetPaths, true);

        private static int RemovePathsFromExclude(IEnumerable<string> assetPaths) =>
            ModifyExclude(assetPaths, false);

        /// <summary>
        /// Adds or removes the exclude entries for a set of asset paths, and reports how many assets
        /// actually changed. Static because the asset context menus reach it without a window open.
        /// </summary>
        private static int ModifyExclude(IEnumerable<string> assetPaths, bool add)
        {
            GitRepositoryInfo repository = GitRepositoryInfo.Locate();
            if (!repository.IsValid) return 0;

            string excludeFilePath = repository.ExcludeFilePath;
            if (!add && !File.Exists(excludeFilePath)) return 0;

            var lines = new List<string>();
            if (File.Exists(excludeFilePath)) lines.AddRange(File.ReadAllLines(excludeFilePath));

            int changedAssets = 0;

            foreach (string assetPath in assetPaths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;

                bool changed = false;
                foreach (string entry in GetExcludeEntries(repository, assetPath))
                {
                    if (add)
                    {
                        if (lines.Contains(entry)) continue;
                        lines.Add(entry);
                        changed = true;
                    }
                    else
                    {
                        // RemoveAll, not Remove: a hand-edited file can hold the same entry twice.
                        if (lines.RemoveAll(line => line == entry) > 0) changed = true;
                    }
                }

                if (changed) changedAssets++;
            }

            if (changedAssets > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(excludeFilePath));
                File.WriteAllLines(excludeFilePath, lines);
                NotifyWindowToRefresh();
            }

            return changedAssets;
        }

        /// <summary>
        /// The exclude lines for one project-relative path: the path itself, plus its .meta when the path
        /// is a Unity asset. Both relative to the repository root rather than the project folder.
        /// </summary>
        internal static IEnumerable<string> GetExcludeEntries(GitRepositoryInfo repository, string projectPath)
        {
            string repositoryPath = repository.ToRepositoryPath(projectPath);

            // No trailing slash for folders, deliberately. A trailing slash restricts a pattern to
            // directories, and a symlinked folder - how this toolset is normally installed - is a blob to
            // Git however happily Unity reports it as a valid folder, so "Assets/LinkedAssets/" silently
            // matched nothing. The bare form covers files, real folders with their contents, and symlinks.
            yield return repositoryPath;

            // Only assets have .meta siblings. ProjectSettings/*.json and the backup folders beside
            // Assets/ do not, and a .meta line for those is just noise in the file.
            if (IsAssetPath(projectPath)) yield return repositoryPath + ".meta";
        }

        /// <summary>True for paths inside the Assets folder, which are the ones carrying .meta files.</summary>
        private static bool IsAssetPath(string projectPath)
        {
            return !string.IsNullOrEmpty(projectPath) &&
                   projectPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal);
        }

        private static void NotifyWindowToRefresh()
        {
            // Resources.FindObjectsOfTypeAll rather than GetWindow: GetWindow focuses the window, which
            // yanked the user out of the Project view every time they used the context menu.
            foreach (var window in Resources.FindObjectsOfTypeAll<GitExcludeManager>())
                window.RefreshFileContents();
        }

        // ---- Project window context menu ---------------------------------------------------------

        private static IEnumerable<string> SelectedAssetPaths()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
        }

        [MenuItem("Assets/Git/Add to Local Exclude", false, 20)]
        public static void ContextMenuAddExclude()
        {
            int changed = AddPathsToExclude(new List<string>(SelectedAssetPaths()));

            // Reports what happened rather than assuming success: re-running on already-excluded items,
            // or running outside a Git repo, both used to log "added" regardless.
            if (changed > 0)
                Debug.Log($"<b>[Git Exclude]</b> Added {changed} item(s) to local exclude.");
            else if (GitRepositoryInfo.Locate().IsValid)
                Debug.Log("<b>[Git Exclude]</b> Selection was already excluded.");
        }

        [MenuItem("Assets/Git/Add to Local Exclude", true)]
        public static bool ContextMenuAddExcludeValidate() => HasAssetSelection();

        [MenuItem("Assets/Git/Remove from Local Exclude", false, 21)]
        public static void ContextMenuRemoveExclude()
        {
            int changed = RemovePathsFromExclude(new List<string>(SelectedAssetPaths()));

            if (changed > 0)
                Debug.Log($"<b>[Git Exclude]</b> Removed {changed} item(s) from local exclude.");
            else if (GitRepositoryInfo.Locate().IsValid)
                Debug.Log("<b>[Git Exclude]</b> Selection was not in the local exclude file.");
        }

        [MenuItem("Assets/Git/Remove from Local Exclude", true)]
        public static bool ContextMenuRemoveExcludeValidate() => HasAssetSelection();

        [MenuItem("Assets/Git/Skip Worktree (keep local changes)", false, 40)]
        public static void ContextMenuSkipWorktree() => SetSkipWorktreeForSelection(GitRepositoryInfo.Locate(), true);

        [MenuItem("Assets/Git/Skip Worktree (keep local changes)", true)]
        public static bool ContextMenuSkipWorktreeValidate() => HasAssetSelection();

        [MenuItem("Assets/Git/Stop Skipping Worktree", false, 41)]
        public static void ContextMenuStopSkipWorktree() => SetSkipWorktreeForSelection(GitRepositoryInfo.Locate(), false);

        [MenuItem("Assets/Git/Stop Skipping Worktree", true)]
        public static bool ContextMenuStopSkipWorktreeValidate() => HasAssetSelection();

        /// <summary>
        /// Applies skip-worktree to the current Project-window selection, expanding folders to the tracked
        /// files they contain. Shared by the window buttons and the context menu.
        /// </summary>
        private static int SetSkipWorktreeForSelection(GitRepositoryInfo repository, bool skip)
        {
            if (!repository.IsValid)
            {
                Debug.LogError("<b>[Git Exclude]</b> No Git repository found in this project folder or any " +
                               "folder above it.");
                return 0;
            }

            List<string> tracked = GitSkipWorktree.ExpandToTrackedFiles(repository, SelectedAssetPaths());
            if (tracked.Count == 0)
            {
                Debug.Log("<b>[Git Exclude]</b> Nothing in the selection is tracked by Git. skip-worktree only " +
                          "applies to tracked files - use the Exclude tab for untracked ones.");
                return 0;
            }

            int changed = GitSkipWorktree.Set(repository, tracked, skip);
            if (changed == 0) return 0;

            if (skip)
            {
                Debug.Log($"<b>[Git Exclude]</b> skip-worktree set on {changed} file(s). Local edits will now " +
                          "survive branch switches, and upstream changes to them will be ignored.");
            }
            else
            {
                Debug.Log($"<b>[Git Exclude]</b> skip-worktree cleared on {changed} file(s). Git may overwrite " +
                          "them on the next checkout.");
            }

            return changed;
        }

        private static int CountSelectedAssets()
        {
            int count = 0;
            foreach (string _ in SelectedAssetPaths()) count++;
            return count;
        }

        private static bool HasAssetSelection()
        {
            foreach (string _ in SelectedAssetPaths()) return true;
            return false;
        }
    }
}
