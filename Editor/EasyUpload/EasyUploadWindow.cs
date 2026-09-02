using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>
    /// Deploy a build folder to one or more S3 buckets without leaving Unity.
    ///
    /// Three steps, in the order the work actually happens: the buckets are standing configuration
    /// for a campaign, the folder changes every build, and the review comes last. Reviewing and
    /// uploading open their own window (<see cref="PlanReviewWindow"/>) so a thousand-file list
    /// never has to fit under the cards.
    ///
    /// Credentials are the same file the EasyUpload desktop app uses, so connecting in either tool
    /// connects both — see <see cref="AwsCredentials.StorePath"/>.
    ///
    /// Everything that talks to AWS runs on background threads; results come back through
    /// <see cref="mainThread"/> because IMGUI may only be touched from the main thread.
    /// </summary>
    public class EasyUploadWindow : EditorWindow
    {
        private enum Tab { Deploy, Settings }

        private static readonly Vector2 MinWindowSize = new Vector2(460, 560);

        private EasyUploadSettings settings;
        private Tab tab = Tab.Deploy;
        private Vector2 pageScroll;

        // ---- connection ----
        private AwsCredentials credentials;
        private string pasteText = "";
        private string connectionMessage = "";
        private string connectionIdentity = "";
        private bool connectionOk;
        private bool connecting;


        // ---- buckets ----
        private List<string> allBuckets = new List<string>();
        private bool loadingBuckets;
        private string bucketError = "";

        // ---- build folder ----
        private string buildPath = "";
        private int folderFiles = -1;
        private long folderBytes;
        private bool scanningFolder;
        private bool dragOver;

        // ---- review ----
        private bool planning;
        private string planStatus = "";
        private string planError = "";
        private CancellationTokenSource planCancellation;

        private SyncPlan plan;
        private string fileFilter = "";

        /// <summary>
        /// Lists the files that are never uploaded at all — .DS_Store and its friends. They show
        /// greyed and cannot be ticked; this only decides whether you can see that they were found.
        /// </summary>
        private bool showDropped;

        /// <summary>
        /// Lists the files the bucket already has an identical copy of, and lets them be ticked so
        /// they can be pushed again.
        ///
        /// Deliberately not a plan input: re-sending is a decision about what to do with the review
        /// in front of you, not about how to build it, so turning it on reveals the rows instead of
        /// throwing the review away and listing every bucket again.
        /// </summary>
        private bool allowResend;

        private int droppedHidden;
        private int unchangedHidden;

        // IMGUI runs Layout and Repaint as two passes over the same code and requires both to emit
        // the same controls. Anything that decides whether a control exists therefore has to be
        // frozen for the pass — background threads and the row rebuild both change these mid-frame,
        // and a mismatch throws "Getting control N's position in a group with only M controls",
        // which takes the whole window down.
        private SyncPlan framePlan;
        private UploadJob frameJob;
        private bool frameJobRunning;
        private int frameJobErrors;
        private bool framePlanning;
        private bool frameCanReview;
        private bool frameHasFolder;

        // Throttled because FreezeFrame runs on every mouse-move frame now, and these hit the
        // filesystem and EditorPrefs. Half a second is far below noticing and far above per-frame.
        private const double ProbeInterval = 0.5;
        private double lastFolderProbe = double.NegativeInfinity;
        private bool folderExists;
        private double lastRootProbe = double.NegativeInfinity;
        private string buildRootCache = "";
        private AwsCredentials frameCredentials;
        private string frameConnectionMessage = "";
        private bool frameConnectionOk;
        private bool frameConnecting;
        private List<Row> frameRows = new List<Row>();
        private Vector2 listScroll;
        private readonly HashSet<string> collapsed = new HashSet<string>();
        private int collapseVersion;

        // Rebuilding the row list on every OnGUI pass means twice a frame, over every file in the
        // plan. Cached against the things that actually change it instead.
        private List<Row> rows = new List<Row>();
        private string rowsKey;

        // ---- upload ----
        private Uploader uploader;
        private UploadJob job;
        private bool assembliesLocked;

        private const float RowHeight = ToolStyles.ListRowHeight;

        /// <summary>A section header (one bucket) or a file under it, flattened so one scroll covers all.</summary>
        private class Row
        {
            public BucketPlan Section;
            public PlanEntry Entry;
        }

        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();

        [MenuItem("Utilities/EasyUpload", false, 1003)]
        public static void ShowWindow()
        {
            GetWindow<EasyUploadWindow>("EasyUpload").minSize = MinWindowSize;
        }

        private void OnEnable()
        {
            // An EditorWindow is only sent mouse-move events if it asks for them, and without them
            // a hover state does not repaint until something else happens to trigger a frame — a
            // click, a scroll, or the ten-times-a-second inspector tick. That is the delay: the
            // styles were already hovering, the window just was not redrawing to show it.
            wantsMouseMove = true;

            settings = EasyUploadSettings.Instance;
            buildPath = settings.lastBuildPath ?? "";
            credentials = AwsCredentials.Load();
            connectionMessage = "";
            EditorApplication.update += OnEditorUpdate;

            if (credentials != null) VerifyConnection();
            if (!string.IsNullOrEmpty(buildPath)) ScanFolder();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            planCancellation?.Cancel();
            uploader?.Cancel();
            Unlock();
            settings.lastBuildPath = buildPath;
            settings.Save();
        }

        private void OnEditorUpdate()
        {
            while (mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            // An animated label is only animated if something asks for the frames.
            if ((job != null && job.Running) || planning || connecting || loadingBuckets) Repaint();
            else if (job != null && assembliesLocked)
            {
                Unlock();
                ScanFolder();
                Repaint();
            }
        }

        private void Unlock()
        {
            if (!assembliesLocked) return;
            EditorApplication.UnlockReloadAssemblies();
            assembliesLocked = false;
        }

        private void Post(Action action) => mainThread.Enqueue(action);

        // ---------- chrome ----------

        private void OnGUI()
        {
            ToolStyles.Ensure();
            ToolStyles.Backdrop(position);

            if (Event.current.type == EventType.MouseMove) Repaint();
            if (Event.current.type == EventType.Layout) FreezeFrame();

            DrawToolbar();

            // The deploy tab is deliberately not inside a scroll view: the review list has to be
            // able to claim the leftover height, and a control cannot expand inside a scroll view
            // that is itself willing to grow forever. Settings is a form, so it still scrolls.
            if (tab == Tab.Deploy)
            {
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ToolStyles.SpaceL);
                    using (new EditorGUILayout.VerticalScope()) DrawDeploy();
                    GUILayout.Space(ToolStyles.SpaceL);
                }
                GUILayout.Space(ToolStyles.SpaceL);
            }
            else
            {
                pageScroll = EditorGUILayout.BeginScrollView(pageScroll);
                GUILayout.Space(ToolStyles.SpaceL);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ToolStyles.SpaceL);
                    using (new EditorGUILayout.VerticalScope()) DrawSettings();
                    GUILayout.Space(ToolStyles.SpaceL);
                }
                GUILayout.Space(ToolStyles.SpaceL);
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// Takes the one snapshot of everything the layout depends on, at the start of the Layout
        /// pass. The Repaint pass that follows then draws from the same picture, whatever the
        /// worker threads have done in between.
        /// </summary>
        private void FreezeFrame()
        {
            framePlan = plan;
            frameJob = job;
            frameJobRunning = job != null && job.Running;
            frameJobErrors = job?.ErrorCount ?? 0;
            framePlanning = planning;
            frameCanReview = CanReview();
            frameHasFolder = BuildFolderExists();
            frameCredentials = credentials;
            frameConnectionMessage = connectionMessage;
            frameConnectionOk = connectionOk;
            frameConnecting = connecting;
            frameRows = framePlan != null ? Rows() : new List<Row>();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Toggle(tab == Tab.Deploy, "Deploy", EditorStyles.toolbarButton, GUILayout.Width(ToolStyles.TabWidth)))
                    tab = Tab.Deploy;
                if (GUILayout.Toggle(tab == Tab.Settings, "Settings", EditorStyles.toolbarButton, GUILayout.Width(ToolStyles.TabWidth)))
                    tab = Tab.Settings;

                GUILayout.FlexibleSpace();

                if (!string.IsNullOrEmpty(settings.endpoint))
                {
                    var previous = GUI.contentColor;
                    GUI.contentColor = ToolStyles.Warn;
                    GUILayout.Label("local S3", EditorStyles.miniLabel);
                    GUI.contentColor = previous;
                    GUILayout.Space(ToolStyles.SpaceM);
                }

                DrawConnectionPill();
                GUILayout.Space(ToolStyles.SpaceS);
            }
        }

        private void DrawConnectionPill()
        {
            string label;
            Color dot;
            string tooltip;

            var measureAs = (string)null;

            if (connecting)
            {
                label = "Checking AWS" + ToolStyles.Ellipsis();
                measureAs = "Checking AWS" + ToolStyles.EllipsisWidest;
                dot = ToolStyles.Muted;
                tooltip = "Asking AWS whether these credentials still work.";
            }
            else if (credentials == null)
            {
                label = "Not connected";
                dot = ToolStyles.Faint;
                tooltip = "Click to paste a credentials block.";
            }
            else if (connectionOk)
            {
                label = "Connected " + credentials.Hint;
                dot = ToolStyles.Ok;
                tooltip = connectionIdentity;
            }
            else
            {
                label = "Session expired";
                dot = ToolStyles.Err;
                tooltip = connectionMessage;
            }

            if (ToolStyles.StatusPill(label, dot, tooltip, measureAs)) tab = Tab.Settings;
        }

        // ---------- deploy ----------

        private void DrawDeploy()
        {
            DrawDestinationCard();
            GUILayout.Space(ToolStyles.SpaceL);
            DrawFolderCard();
            GUILayout.Space(ToolStyles.SpaceL);
            DrawUploadCard();

            // Nothing to review yet, so the cards sit at the top rather than stretching.
            if (plan == null) GUILayout.FlexibleSpace();
        }

        /// <summary>
        /// Step 1: where it goes. First because it is the part that stays put — you pick the buckets
        /// once for a campaign and then deploy into them over and over.
        /// </summary>
        private void DrawDestinationCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var connected = credentials != null;
                var header = ToolStyles.CardHeader(1, "Destination",
                    connected && settings.buckets.Count > 0);

                var buttonRect = new Rect(header.xMax - ToolStyles.ButtonL, header.y + 1,
                    ToolStyles.ButtonL, ToolStyles.ControlHeight);
                using (new ToolStyles.DisabledScope(!connected))
                {
                    if (GUI.Button(buttonRect, settings.buckets.Count > 0 ? "Change buckets…" : "Choose buckets…",
                            ToolStyles.Secondary))
                    {
                        var anchor = GUIUtility.GUIToScreenRect(buttonRect);
                        if (allBuckets.Count == 0 && !loadingBuckets) RefreshBuckets();
                        BucketPickerWindow.Open(anchor, settings, () => allBuckets, Repaint);
                    }
                }

                GUILayout.Space(ToolStyles.SpaceM);

                if (!connected)
                {
                    EditorGUILayout.HelpBox("Connect to AWS first — click the status chip at the top right.",
                        MessageType.Info);
                    return;
                }

                if (!string.IsNullOrEmpty(bucketError))
                    EditorGUILayout.HelpBox(bucketError, MessageType.Error);

                if (settings.buckets.Count == 0)
                {
                    ToolStyles.ValueBox("", loadingBuckets ? "Loading buckets…" : "No buckets selected");
                }
                else
                {
                    DrawBucketTags();
                }

                GUILayout.Space(ToolStyles.SpaceM);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Version folder", ToolStyles.RowLabel, GUILayout.Width(ToolStyles.FormLabelWidth));
                    var index = Math.Max(0, Array.IndexOf(EasyUploadSettings.Versions, settings.version));
                    var chosen = EditorGUILayout.Popup(index, EasyUploadSettings.Versions, GUILayout.Width(ToolStyles.PopupWidth));
                    if (chosen != index)
                    {
                        settings.version = EasyUploadSettings.Versions[chosen];
                        settings.Save();
                    }
                    GUILayout.FlexibleSpace();
                }

                // Every destination in full: changing the version moves all of them together, and a
                // "+2 more" would hide exactly the thing this line exists to confirm.
                if (settings.buckets.Count > 0)
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    // Same reason as the credentials path: a non-wrapping label reports its content
                    // width as its minimum, so a long bucket name would widen the card rather than
                    // being clipped by it. Reserve the row, then fit the text to it.
                    foreach (var bucket in settings.buckets)
                    {
                        var line = "→ s3://" + bucket + "/" + UploadPlanner.KeyPrefix(settings.version);
                        var lineRect = GUILayoutUtility.GetRect(0, 15, GUILayout.ExpandWidth(true));
                        GUI.Label(lineRect,
                            new GUIContent(ToolStyles.Elide(line,
                                ToolStyles.MonoCharsFor(lineRect.width)), line),
                            ToolStyles.MonoSmall);
                    }
                }
            }
        }

        /// <summary>Bucket tags, wrapped by hand — IMGUI has no flow layout.</summary>
        private void DrawBucketTags()
        {
            var available = EditorGUIUtility.currentViewWidth - 62;
            string remove = null;

            EditorGUILayout.BeginHorizontal();
            var used = 0f;

            foreach (var bucket in settings.buckets)
            {
                var width = ToolStyles.MonoSmall.CalcSize(new GUIContent(bucket)).x + 34;
                if (used > 0 && used + width > available)
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(ToolStyles.SpaceS);
                    EditorGUILayout.BeginHorizontal();
                    used = 0;
                }

                if (ToolStyles.RemovableTag(bucket)) remove = bucket;
                GUILayout.Space(ToolStyles.SpaceS);
                used += width + 4;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (remove == null) return;
            settings.buckets.Remove(remove);
            settings.Save();
        }

        /// <summary>Step 2: what goes. Drag-first, because this is the part that changes every build.</summary>
        private void DrawFolderCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                var hasFolder = frameHasFolder;
                var header = ToolStyles.CardHeader(2, "Build folder", hasFolder);

                // Both ways of naming a folder, together and styled alike — one picks it by hand,
                // the other takes it from the build. Separating them put the same decision in two
                // different places in two different weights.
                var chooseRect = new Rect(header.xMax - ToolStyles.ButtonL, header.y + 1,
                    ToolStyles.ButtonL, ToolStyles.ControlHeight);
                if (GUI.Button(chooseRect, hasFolder ? "Change folder…" : "Choose folder…",
                        ToolStyles.Secondary))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Choose a build folder", buildPath, "");
                    if (!string.IsNullOrEmpty(chosen)) SetBuildFolder(chosen);
                }

                DrawFromBuildButton(new Rect(
                    chooseRect.x - ToolStyles.SpaceS - ToolStyles.ButtonM, header.y + 1,
                    ToolStyles.ButtonM, ToolStyles.ControlHeight));

                GUILayout.Space(ToolStyles.SpaceM);
                DrawDropZone(hasFolder);

                if (hasFolder)
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(FolderSummary(), ToolStyles.Hint);
                        GUILayout.FlexibleSpace();

                        if (GUILayout.Button("Reveal", ToolStyles.Secondary,
                                GUILayout.Width(ToolStyles.ButtonS),
                                GUILayout.Height(ToolStyles.ControlHeight)))
                            EditorUtility.RevealInFinder(buildPath);

                        if (GUILayout.Button("Clear", ToolStyles.Secondary,
                                GUILayout.Width(ToolStyles.ButtonS),
                                GUILayout.Height(ToolStyles.ControlHeight)))
                            SetBuildFolder("");
                    }
                }

                if (!hasFolder && !string.IsNullOrEmpty(buildPath))
                    EditorGUILayout.HelpBox("That folder is not there any more:\n" + buildPath, MessageType.Warning);
            }
        }

        /// <summary>
        /// Takes the build tool's output folder itself, so the usual round trip — build, find the
        /// folder in Finder, drag it back here — collapses to one click.
        ///
        /// The root, not a folder inside it: the bundles' paths under it are the keys they upload
        /// to, so picking a folder further down would silently strip that prefix off every key.
        /// </summary>
        private void DrawFromBuildButton(Rect rect)
        {
            var root = BuildOutputLocator.BuildToolPresent ? BuildRoot() : "";
            var usable = !string.IsNullOrEmpty(root) && Directory.Exists(root);

            var tooltip = usable
                ? "Take the MTX bundle build's output folder:\n" + root
                : BuildOutputLocator.Explain();

            using (new ToolStyles.DisabledScope(!usable))
            {
                if (GUI.Button(rect, new GUIContent("From build", tooltip), ToolStyles.Secondary))
                    SetBuildFolder(root);
            }
        }

        private void DrawDropZone(bool hasFolder)
        {
            var rect = GUILayoutUtility.GetRect(0, ToolStyles.DropZoneHeight, GUILayout.ExpandWidth(true));

            var fill = dragOver
                ? ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.Accent, 0.25f)
                : ToolStyles.InsetBg;
            var edge = dragOver ? ToolStyles.Accent : ToolStyles.Faint;

            EditorGUI.DrawRect(rect, fill);
            ToolStyles.DashedBorder(rect, edge, 5f, 4f, dragOver ? 2f : 1f);

            if (dragOver)
            {
                GUI.Label(rect, "Release to use this folder", ToolStyles.Centred(ToolStyles.CardTitle));
            }
            else if (hasFolder)
            {
                var top = new Rect(rect.x + 10, rect.y + 8, rect.width - 20, 18);
                GUI.Label(top, new GUIContent(Path.GetFileName(buildPath.TrimEnd('/', '\\')), buildPath),
                    ToolStyles.Centred(ToolStyles.CardTitle));

                var bottom = new Rect(rect.x + 10, rect.y + 26, rect.width - 20, 16);
                GUI.Label(bottom, new GUIContent(Shorten(buildPath, 60), buildPath),
                    ToolStyles.Centred(ToolStyles.MonoSmall));
            }
            else
            {
                // Centred as a two-line block rather than positioned line by line, so it sits in
                // the middle of the box whatever the box height becomes.
                const float lineOne = 18f;
                const float lineTwo = 16f;
                var top = rect.y + (rect.height - (lineOne + lineTwo)) / 2f;

                GUI.Label(new Rect(rect.x + 10, top, rect.width - 20, lineOne),
                    "Drag a build folder here", ToolStyles.Centred(ToolStyles.CardTitle));

                GUI.Label(new Rect(rect.x + 10, top + lineOne, rect.width - 20, lineTwo),
                    "from Finder or the Project window", ToolStyles.Centred(ToolStyles.Hint));
            }

            HandleDrop(rect);
        }

        /// <summary>
        /// Step 3: check, then send. The review lives here rather than in a window of its own — it
        /// is the same task as pressing the button, and a second window to look at what one click
        /// is about to do is a window too many.
        /// </summary>
        private void DrawUploadCard()
        {
            var reviewing = framePlan != null;
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card,
                       reviewing ? GUILayout.ExpandHeight(true) : GUILayout.ExpandHeight(false)))
            {
                ToolStyles.CardHeader(3, "Review & upload", reviewing && framePlan.TotalSelected == 0);
                GUILayout.Space(ToolStyles.SpaceM);

                DrawReviewControls();

                if (!reviewing)
                {
                    if (!string.IsNullOrEmpty(planError))
                        EditorGUILayout.HelpBox(planError, MessageType.Error);
                    return;
                }

                GUILayout.Space(ToolStyles.SpaceM);
                DrawResultsBar();
                GUILayout.Space(ToolStyles.SpaceS);

                var listRect = GUILayoutUtility.GetRect(0, 80,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                DrawList(listRect, frameRows);

                GUILayout.Space(ToolStyles.SpaceM);
                DrawUploadFooter();
            }
        }

        private bool CanReview() =>
            credentials != null && BuildFolderExists() && settings.buckets.Count > 0;

        /// <summary>Whether the chosen folder is still there, asked of the disk at most twice a second.</summary>
        private bool BuildFolderExists()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - lastFolderProbe > ProbeInterval)
            {
                lastFolderProbe = now;
                folderExists = !string.IsNullOrEmpty(buildPath) && Directory.Exists(buildPath);
            }
            return folderExists;
        }

        /// <summary>The build tool's output path, on the same throttle.</summary>
        private string BuildRoot()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - lastRootProbe > ProbeInterval)
            {
                lastRootProbe = now;
                buildRootCache = BuildOutputLocator.Root();
            }
            return buildRootCache;
        }

        private void DrawReviewControls()
        {
            var ready = frameCanReview && !framePlanning && !frameJobRunning;

            // Left-aligned, like the other main actions: the button that starts the panel's work
            // sits where reading starts, and the supporting controls trail after it.
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new ToolStyles.DisabledScope(!ready))
                {
                    // While a review runs the button says what it is doing, rather than saying
                    // "Reviewing…" with a line underneath saying the same thing in more words.
                    // Its own trailing dots are stripped so the animated ones are the only ones.
                    var label = framePlanning
                        ? (string.IsNullOrEmpty(planStatus) ? "Reviewing" : planStatus.TrimEnd('…', '.', ' '))
                          + ToolStyles.Ellipsis()
                        : framePlan == null ? "Review what will change" : "Review again";

                    // What is still missing lives on the button it is disabling, rather than as a
                    // line of text under it. The button is visibly dimmed either way; the tooltip
                    // answers "why" for whoever asks.
                    var content = new GUIContent(label, ready ? "" : WhatIsMissing());
                    if (GUILayout.Button(content, ToolStyles.Primary,
                            GUILayout.Height(ToolStyles.ActionHeight), GUILayout.MinWidth(170)))
                        StartReview();
                }

                if (framePlanning)
                {
                    GUILayout.Space(ToolStyles.SpaceS);
                    if (GUILayout.Button("Stop", ToolStyles.Secondary,
                            GUILayout.Height(ToolStyles.ActionHeight),
                            GUILayout.Width(ToolStyles.ButtonS)))
                        planCancellation?.Cancel();
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void DrawResultsBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var selected = framePlan.TotalSelected;
                var summary = selected == 0
                    ? "Everything is up to date"
                    : selected + (selected == 1 ? " file · " : " files · ") +
                      UploadPlanner.HumanBytes(framePlan.TotalBytes) + " to send";
                GUILayout.Label(summary, ToolStyles.CardTitle);

                GUILayout.FlexibleSpace();

                var hidden = (showDropped ? 0 : droppedHidden) + (allowResend ? 0 : unchangedHidden);
                if (hidden > 0)
                {
                    var parts = new List<string>();
                    if (!allowResend && unchangedHidden > 0)
                        parts.Add(unchangedHidden + " already up to date — Re-send lists them");
                    if (!showDropped && droppedHidden > 0)
                        parts.Add(droppedHidden + " never uploaded — Show dropped lists them");

                    GUILayout.Label(new GUIContent(hidden + " hidden", string.Join("\n", parts.ToArray())),
                        ToolStyles.StatusText, GUILayout.Width(ToolStyles.MetaWidth));
                }

                fileFilter = EditorGUILayout.TextField(fileFilter, EditorStyles.toolbarSearchField,
                    GUILayout.Width(ToolStyles.FieldWidth));
            }

            if (framePlan.Truncated)
                EditorGUILayout.HelpBox("This folder holds " + framePlan.Files.Count + " files; only the first "
                    + UploadPlanner.MaxPlanEntries + " are listed, and only those will be uploaded.",
                    MessageType.Warning);
        }

        /// <summary>Section headers and file rows as one flat list, rebuilt only when something changed.</summary>
        private List<Row> Rows()
        {
            var key = fileFilter + "\u0000" + showDropped + "\u0000" + allowResend +
                      "\u0000" + collapseVersion + "\u0000" + plan.GetHashCode();
            if (key == rowsKey) return rows;

            var needle = (fileFilter ?? "").Trim();
            var built = new List<Row>();
            var dropped = 0;
            var unchanged = 0;

            foreach (var bucketPlan in plan.Buckets)
            {
                built.Add(new Row { Section = bucketPlan });
                var listing = !collapsed.Contains(bucketPlan.Bucket) && string.IsNullOrEmpty(bucketPlan.Error);

                foreach (var entry in bucketPlan.Entries)
                {
                    // Each switch owns one category, and neither hides anything that is going to be
                    // sent. Filtering is on the verdict and never on the tick — a row that vanished
                    // the moment you unticked it would make the list impossible to work through.
                    //
                    // Over-5GB files are deliberately in neither category: they are silently not
                    // uploaded, which is exactly the thing that must not be possible to hide.
                    if (entry.Reason == UploadReason.Junk)
                    {
                        dropped++;
                        if (!showDropped) continue;
                    }
                    else if (entry.Reason == UploadReason.UpToDate)
                    {
                        unchanged++;
                        if (!allowResend) continue;
                    }

                    // Counted above whether or not the bucket is expanded, so collapsing one moves
                    // no numbers — only what is drawn.
                    if (!listing) continue;
                    if (needle.Length > 0 &&
                        entry.File.Relative.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    built.Add(new Row { Entry = entry });
                }
            }

            rows = built;
            rowsKey = key;
            droppedHidden = dropped;
            unchangedHidden = unchanged;
            return rows;
        }

        private void DrawList(Rect rect, List<Row> visible)
        {
            GUI.Box(rect, GUIContent.none, ToolStyles.Inset);
            var inner = new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2);
            var content = new Rect(0, 0,
                ToolStyles.ListContentWidth(inner, visible.Count, RowHeight),
                visible.Count * RowHeight);

            listScroll = GUI.BeginScrollView(inner, listScroll, content);

            // Only the rows on screen are drawn: IMGUI charges for every control whether or not it
            // is visible, and a build folder can hold thousands of files.
            var first = Mathf.Max(0, Mathf.FloorToInt(listScroll.y / RowHeight));
            var last = Mathf.Min(visible.Count, first + Mathf.CeilToInt(inner.height / RowHeight) + 1);

            for (var i = first; i < last; i++)
            {
                var row = new Rect(0, i * RowHeight, content.width, RowHeight);
                if (visible[i].Section != null) DrawSectionRow(row, visible[i].Section);
                else DrawFileRow(row, visible[i].Entry, i);
            }

            GUI.EndScrollView();
        }

        private void DrawSectionRow(Rect rect, BucketPlan bucketPlan)
        {
            EditorGUI.DrawRect(rect, ToolStyles.Blend(ToolStyles.InsetBg, ToolStyles.CardBg, 0.9f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), ToolStyles.CardBorder);

            var isCollapsed = collapsed.Contains(bucketPlan.Bucket);
            if (GUI.Button(new Rect(rect.x + 4, rect.y + 2, 14, 16), isCollapsed ? "▶" : "▼", EditorStyles.label))
            {
                if (isCollapsed) collapsed.Remove(bucketPlan.Bucket);
                else collapsed.Add(bucketPlan.Bucket);
                collapseVersion++;
            }

            var failed = !string.IsNullOrEmpty(bucketPlan.Error);
            ToolStyles.Dot(new Rect(rect.x + 20, rect.y + 8, 6, 6),
                failed ? ToolStyles.Err
                    : bucketPlan.SelectedCount > 0 ? ToolStyles.Accent : ToolStyles.Ok);

            GUI.Label(new Rect(rect.x + 32, rect.y, Mathf.Max(60, rect.width - 290), RowHeight),
                "s3://" + bucketPlan.Bucket + "/" + bucketPlan.Prefix, ToolStyles.Mono);

            if (failed)
            {
                ToolStyles.ColouredLabel(new Rect(rect.xMax - 250, rect.y, 246, RowHeight),
                    new GUIContent(bucketPlan.Error, bucketPlan.Error), ToolStyles.StatusText,
                    ToolStyles.Err);
                return;
            }

            GUI.Label(new Rect(rect.xMax - 250, rect.y, 158, RowHeight),
                bucketPlan.SelectedCount + " of " + bucketPlan.Entries.Count + " · " +
                UploadPlanner.HumanBytes(bucketPlan.SelectedBytes), ToolStyles.StatusText);

            using (new ToolStyles.DisabledScope(frameJobRunning))
            {
                if (GUI.Button(new Rect(rect.xMax - 88, rect.y + 2, 40, ToolStyles.InRowHeight), "All", ToolStyles.SecondaryCompact))
                    SetAll(bucketPlan, true);
                if (GUI.Button(new Rect(rect.xMax - 44, rect.y + 2, 40, ToolStyles.InRowHeight), "None", ToolStyles.SecondaryCompact))
                    SetAll(bucketPlan, false);
            }
        }

        private void DrawFileRow(Rect rect, PlanEntry entry, int index)
        {
            if (index % 2 == 1)
                EditorGUI.DrawRect(rect, new Color(0, 0, 0, EditorGUIUtility.isProSkin ? 0.08f : 0.03f));

            var tickable = Tickable(entry);
            using (new ToolStyles.DisabledScope(!tickable || frameJobRunning))
            {
                var now = EditorGUI.Toggle(new Rect(rect.x + 8, rect.y + 3, 16, 16), entry.Selected);
                if (now != entry.Selected && tickable) entry.Selected = now;
            }

            const float statusWidth = 92f;
            const float sizeWidth = 70f;
            var nameRect = new Rect(rect.x + 28, rect.y,
                Mathf.Max(40, rect.width - 28 - statusWidth - sizeWidth - 8), RowHeight);

            var previous = GUI.contentColor;
            if (!tickable) GUI.contentColor = ToolStyles.Faint;
            GUI.Label(nameRect, new GUIContent(entry.File.Relative, entry.File.Relative), ToolStyles.MonoSmall);
            GUI.contentColor = previous;

            var sizeRect = new Rect(nameRect.xMax, rect.y, sizeWidth, RowHeight);
            GUI.Label(sizeRect, UploadPlanner.HumanBytes(entry.File.Size), ToolStyles.StatusText);

            ToolStyles.ColouredLabel(new Rect(sizeRect.xMax + 8, rect.y, statusWidth - 8, RowHeight),
                new GUIContent(UploadPlanner.Describe(entry.Reason), UploadPlanner.Explain(entry.Reason)),
                ToolStyles.StatusText, ReasonColor(entry.Reason));
        }

        /// <summary>
        /// Whether this row's box can be ticked. OS files and anything over the single-PUT ceiling
        /// never can; a file the bucket already has an identical copy of only can once Re-send is on.
        /// </summary>
        private bool Tickable(PlanEntry entry) =>
            entry.CanUpload && (allowResend || entry.Reason != UploadReason.UpToDate);

        private static Color ReasonColor(UploadReason reason)
        {
            switch (reason)
            {
                case UploadReason.New: return ToolStyles.Accent;
                case UploadReason.Size:
                case UploadReason.Newer:
                case UploadReason.Forced: return ToolStyles.Warn;
                case UploadReason.TooLarge: return ToolStyles.Err;
                default: return ToolStyles.Muted;
            }
        }

        private void SetAll(BucketPlan bucketPlan, bool value)
        {
            foreach (var entry in bucketPlan.Entries)
                if (Tickable(entry)) entry.Selected = value;
        }

        private void DrawUploadFooter()
        {
            if (frameJob != null)
            {
                var bar = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                // frameJob, not job: the guard above is the frozen one, and clearing the folder
                // nulls the live field part way through a pass that has already committed to
                // drawing a progress bar.
                EditorGUI.ProgressBar(bar, frameJob.Fraction,
                    frameJob.DoneFiles + " / " + frameJob.TotalFiles + " files · " +
                    UploadPlanner.HumanBytes(frameJob.DoneBytes) + " of " +
                    UploadPlanner.HumanBytes(frameJob.TotalBytes));

                GUILayout.Space(ToolStyles.SpaceXS);
                ToolStyles.ColouredLabel(frameJob.Status, ToolStyles.Hint,
                    frameJobErrors > 0 ? ToolStyles.Err : ToolStyles.Muted);

                if (frameJobErrors > 0 && !frameJobRunning)
                {
                    var errors = frameJob.Errors;
                    var shown = Mathf.Min(3, errors.Count);
                    var text = string.Join("\n", errors.GetRange(0, shown).ToArray());
                    if (errors.Count > shown) text += "\n…and " + (errors.Count - shown) + " more.";
                    EditorGUILayout.HelpBox(text, MessageType.Error);
                }

                GUILayout.Space(ToolStyles.SpaceS);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (frameJobRunning)
                {
                    if (GUILayout.Button("Stop", ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonS),
                            GUILayout.Height(ToolStyles.ActionHeight))) uploader?.Cancel();
                    GUILayout.Label("Script reloading is held until this finishes.", ToolStyles.Hint);
                    GUILayout.FlexibleSpace();
                    return;
                }

                // Upload first, like Review and Connect, with the two switches beside it because
                // they decide what it is about to send.
                //
                // There is no Clear here: step 2's Clear already drops the folder and the review
                // with it, and Review again replaces the review outright. A second button for a
                // state you cannot get stuck in is a button that only has to be understood.
                var selected = framePlan.TotalSelected;
                using (new ToolStyles.DisabledScope(selected == 0))
                {
                    var label = selected == 0
                        ? "Nothing to upload"
                        : "Upload " + selected + (selected == 1 ? " file · " : " files · ") +
                          UploadPlanner.HumanBytes(framePlan.TotalBytes);
                    if (GUILayout.Button(label, ToolStyles.Primary, GUILayout.Height(ToolStyles.ActionHeight),
                            GUILayout.MinWidth(190)))
                        StartUpload();
                }

                GUILayout.Space(ToolStyles.SpaceL);

                var show = GUILayout.Toggle(showDropped, new GUIContent("Show dropped",
                    "List the files that are never uploaded — .DS_Store, Thumbs.db, desktop.ini. "
                    + "They show greyed and cannot be ticked."), GUILayout.ExpandWidth(false));
                if (show != showDropped)
                {
                    showDropped = show;
                    rowsKey = null;
                }

                GUILayout.Space(ToolStyles.SpaceL);

                var previous = GUI.contentColor;
                if (allowResend) GUI.contentColor = ToolStyles.Warn;
                var resend = GUILayout.Toggle(allowResend, new GUIContent("Re-send",
                    "List the files the bucket already has an identical copy of, so you can tick the "
                    + "ones you want to push again. Nothing is ticked for you."),
                    GUILayout.ExpandWidth(false));
                GUI.contentColor = previous;

                if (resend != allowResend)
                {
                    allowResend = resend;
                    rowsKey = null;
                    // Turning it off takes the rows away, so it takes their ticks with them —
                    // otherwise Upload would keep sending files that are no longer on screen.
                    if (!allowResend) ClearUnchangedSelection();
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void ClearUnchangedSelection()
        {
            if (plan == null) return;
            foreach (var bucketPlan in plan.Buckets)
                foreach (var entry in bucketPlan.Entries)
                    if (entry.Reason == UploadReason.UpToDate) entry.Selected = false;
        }

        private void StartUpload()
        {
            var identical = 0;
            foreach (var bucketPlan in plan.Buckets)
            foreach (var entry in bucketPlan.Entries)
                if (entry.Selected && entry.CanUpload &&
                    (entry.Reason == UploadReason.UpToDate || entry.Reason == UploadReason.Forced)) identical++;

            if (identical > 0 && !EditorUtility.DisplayDialog("Re-send identical files?",
                    identical + " of the ticked files are identical to what is already in the bucket. "
                    + "They will be overwritten with the same content.",
                    "Upload anyway", "Cancel"))
            {
                return;
            }

            // A script recompile mid-deploy tears down the worker threads and leaves you guessing
            // which files made it. Hold reloads until the upload is finished.
            if (!assembliesLocked)
            {
                EditorApplication.LockReloadAssemblies();
                assembliesLocked = true;
            }

            uploader = new Uploader();
            job = uploader.Start(NewClient(), plan, settings.concurrency);
            Repaint();
        }

        private string WhatIsMissing()
        {
            if (credentials == null) return "Connect to AWS to continue.";
            if (settings.buckets.Count == 0) return "Choose at least one bucket in step 1.";
            if (string.IsNullOrEmpty(buildPath) || !Directory.Exists(buildPath))
                return "Drop a build folder in step 2.";
            return "";
        }

        // ---------- settings ----------

        private void DrawSettings()
        {
            DrawConnectionCard();
            GUILayout.Space(ToolStyles.SpaceL);

            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                GUILayout.Label("Advanced", ToolStyles.CardTitle);
                GUILayout.Space(ToolStyles.SpaceM);

                EditorGUI.BeginChangeCheck();
                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 150;

                settings.portalUrl = EditorGUILayout.TextField(new GUIContent("AWS portal address",
                    "Where “Open AWS portal” goes. The browsable portal is the awsapps.com address, "
                    + "not the SSO start URL AWS shows on its Get credentials screen."), settings.portalUrl);

                settings.region = EditorGUILayout.TextField(new GUIContent("Discovery region",
                    "Only used to list buckets and check credentials. Each bucket's own region is "
                    + "detected automatically at upload time."), settings.region);

                settings.endpoint = EditorGUILayout.TextField(new GUIContent("S3 endpoint",
                    "Point at a local S3 server instead of AWS, for testing. Empty means AWS."),
                    settings.endpoint);

                settings.concurrency = EditorGUILayout.IntSlider(new GUIContent("Uploads in flight",
                    "Game assets are small, so a deploy spends its time on round trips rather than bandwidth."),
                    settings.concurrency, 1, 32);

                EditorGUIUtility.labelWidth = labelWidth;

                if (EditorGUI.EndChangeCheck())
                {
                    settings.Save();
                    allBuckets.Clear();
                }

                GUILayout.Space(ToolStyles.SpaceM);
                GUILayout.Label("Files land at s3://&lt;bucket&gt;/&lt;version&gt;/… mirroring the build folder. "
                    + "Nothing is ever deleted from a bucket.", ToolStyles.Hint);
            }

            GUILayout.Space(ToolStyles.SpaceL);
            DrawDroppedFilesCard();
        }

        /// <summary>
        /// Status first, paste box second.
        ///
        /// The old layout led with a 64-pixel text box that is empty for all but the ten seconds a
        /// year it is used, and buried the one thing worth knowing — whether the session still works
        /// — under it. This says the state, and folds the paste flow away once there is a state to
        /// report.
        /// </summary>
        private void DrawConnectionCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                GUILayout.Label("Connection", ToolStyles.CardTitle);
                GUILayout.Space(ToolStyles.SpaceM);

                DrawConnectionStatus();
                ToolStyles.Divider();
                DrawPasteSection();
                ToolStyles.Divider();
                DrawCredentialStorage();
            }
        }

        private void DrawConnectionStatus()
        {
            string headline;
            Color dot;

            if (frameConnecting)
            {
                headline = "Checking with AWS" + ToolStyles.Ellipsis();
                dot = ToolStyles.Muted;
            }
            else if (frameCredentials == null)
            {
                headline = "Not connected";
                dot = ToolStyles.Faint;
            }
            else if (frameConnectionOk)
            {
                headline = "Connected";
                dot = ToolStyles.Ok;
            }
            else
            {
                headline = "AWS rejected these credentials";
                dot = ToolStyles.Err;
            }

            const float actionsWidth = 2 * 72f + ToolStyles.SpaceS;
            var rect = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            var textWidth = Mathf.Max(80f, rect.width - 20f - actionsWidth);

            ToolStyles.Dot(new Rect(rect.x + 2, rect.y + 5, 8, 8), dot);
            GUI.Label(new Rect(rect.x + 18, rect.y - 2, textWidth, 18), headline, ToolStyles.CardTitle);

            var detail = StatusDetail();
            if (!string.IsNullOrEmpty(detail))
            {
                // Elided rather than wrapped: an ARN is one unbroken token, and letting it wrap is
                // what pushes this panel around in the first place.
                GUI.Label(new Rect(rect.x + 18, rect.y + 15, textWidth, 16),
                    new GUIContent(ToolStyles.Elide(detail, ToolStyles.MonoCharsFor(textWidth)), detail),
                    ToolStyles.MonoSmall);
            }

            using (new ToolStyles.DisabledScope(frameCredentials == null || frameConnecting))
            {
                if (GUI.Button(new Rect(rect.xMax - actionsWidth, rect.y + 4, 72, ToolStyles.ControlHeight), "Re-check",
                        ToolStyles.Secondary))
                    VerifyConnection();

                if (GUI.Button(new Rect(rect.xMax - 72, rect.y + 4, 72, ToolStyles.ControlHeight),
                        new GUIContent("Forget", "Delete the stored credentials."),
                        ToolStyles.Secondary))
                {
                    AwsCredentials.Forget();
                    credentials = null;
                    connectionOk = false;
                    connectionIdentity = "";
                    connectionMessage = "";
                    allBuckets.Clear();
                }
            }

            // The error goes in a box rather than the elided line, because an error is the one thing
            // here you need to read in full.
            if (!frameConnectionOk && !string.IsNullOrEmpty(frameConnectionMessage))
            {
                GUILayout.Space(ToolStyles.SpaceXS);
                EditorGUILayout.HelpBox(frameConnectionMessage, MessageType.Warning);
            }
        }

        private string StatusDetail()
        {
            if (frameCredentials == null) return "Paste a credentials block to begin.";

            var parts = new List<string>();
            if (frameConnectionOk && !string.IsNullOrEmpty(connectionIdentity)) parts.Add(connectionIdentity);
            parts.Add("key " + frameCredentials.Hint);
            if (frameCredentials.savedAt > 0)
                parts.Add("added " + Ago(DateTimeOffset.FromUnixTimeSeconds(frameCredentials.savedAt).UtcDateTime));

            return string.Join("  ·  ", parts.ToArray());
        }

        private void DrawPasteSection()
        {
            GUILayout.Label("Sign in, open Access keys, and copy the block under “macOS and Linux”. "
                + "The three export lines are enough.", ToolStyles.Hint);
            GUILayout.Space(ToolStyles.SpaceS);

            // One height option shared by all three, so they cannot drift apart. The portal button
            // sizes to its own label rather than to a fixed width — "Open AWS portal ↗" is wider
            // than ButtonL and was being clipped by it.
            var buttonHeight = GUILayout.Height(ToolStyles.ControlHeight);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Open AWS portal ↗", ToolStyles.Secondary,
                        buttonHeight, GUILayout.ExpandWidth(false)))
                    Application.OpenURL(settings.portalUrl);

                GUILayout.Space(ToolStyles.SpaceS);

                if (GUILayout.Button("Paste", ToolStyles.Secondary,
                        GUILayout.Width(ToolStyles.ButtonS), buttonHeight))
                    pasteText = EditorGUIUtility.systemCopyBuffer ?? "";

                GUILayout.Space(ToolStyles.SpaceS);

                using (new ToolStyles.DisabledScope(string.IsNullOrEmpty(pasteText)))
                {
                    if (GUILayout.Button("Clear", ToolStyles.Secondary,
                            GUILayout.Width(ToolStyles.ButtonS), buttonHeight))
                    {
                        pasteText = "";
                        GUIUtility.keyboardControl = 0;
                    }
                }
            }

            GUILayout.Space(ToolStyles.SpaceS);

            // PasteBox word-wraps. The default TextArea overload does not, and a session token is a
            // single thousand-character line whose minimum width then forces the window open.
            pasteText = EditorGUILayout.TextArea(pasteText, ToolStyles.TextArea,
                GUILayout.Height(ToolStyles.TextAreaHeight), GUILayout.ExpandWidth(true));

            GUILayout.Space(ToolStyles.SpaceS);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new ToolStyles.DisabledScope(frameConnecting || string.IsNullOrWhiteSpace(pasteText)))
                {
                    var connectLabel = frameConnecting
                        ? "Connecting" + ToolStyles.Ellipsis()
                        : "Connect";
                    if (GUILayout.Button(connectLabel, ToolStyles.Primary,
                            GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ActionHeight)))
                        Connect();
                }

                GUILayout.FlexibleSpace();
            }
        }

        private void DrawCredentialStorage()
        {
            var remember = EditorGUILayout.ToggleLeft(new GUIContent("Remember between launches",
                    "Off keeps them in memory for this session only and writes nothing to disk."),
                settings.rememberCredentials);
            if (remember != settings.rememberCredentials)
            {
                settings.rememberCredentials = remember;
                settings.Save();
                if (!remember) AwsCredentials.Forget();
                else if (credentials != null) AwsCredentials.Save(credentials);
            }

            GUILayout.Space(ToolStyles.SpaceS);

            GUILayout.Label("Shared with the EasyUpload desktop app", ToolStyles.Hint);

            // The row is reserved before the text is measured, and drawn with GUI rather than
            // GUILayout. A non-wrapping label reports its content width as its minimum, so sizing it
            // from anything other than the space it actually has is what pushed this tab past the
            // edge of the window.
            var row = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
            var showRect = new Rect(row.xMax - 50, row.y, 50, 16);
            var pathRect = new Rect(row.x, row.y, Mathf.Max(40f, row.width - 58f), 16);

            var storePath = AwsCredentials.StorePath;
            GUI.Label(pathRect,
                new GUIContent(ToolStyles.Elide(storePath,
                    ToolStyles.MonoCharsFor(pathRect.width)), storePath),
                ToolStyles.MonoSmall);

            if (GUI.Button(showRect, "Show", ToolStyles.Secondary))
                EditorUtility.RevealInFinder(AwsCredentials.ConfigDir);
        }

        /// <summary>
        /// The names that are walked and listed but never uploaded.
        ///
        /// Editable because a build folder picks up whatever the machine that made it leaves behind,
        /// and the three defaults only cover the ones every Mac and Windows box produces.
        /// </summary>
        private void DrawDroppedFilesCard()
        {
            using (new EditorGUILayout.VerticalScope(ToolStyles.Card))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Dropped files", ToolStyles.CardTitle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(settings.droppedFiles.Count + " pattern" +
                                    (settings.droppedFiles.Count == 1 ? "" : "s"), ToolStyles.StatusText);
                }

                GUILayout.Space(ToolStyles.SpaceXS);
                GUILayout.Label("Never uploaded, at any depth. Matched on the file name, so “*” and “?” "
                    + "work — “._*” covers the AppleDouble files a copy to a non-Mac volume leaves behind. "
                    + "They still appear in the review under Show dropped, greyed and untickable.",
                    ToolStyles.Hint);
                GUILayout.Space(ToolStyles.SpaceM);

                // Mutations are deferred to after the loop: removing an entry mid-loop would change
                // the control count partway through the pass, which is what breaks an IMGUI window.
                var removeAt = -1;
                var changed = false;

                for (var i = 0; i < settings.droppedFiles.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var edited = EditorGUILayout.TextField(settings.droppedFiles[i]);
                        if (edited != settings.droppedFiles[i])
                        {
                            settings.droppedFiles[i] = edited;
                            changed = true;
                        }

                        if (GUILayout.Button(new GUIContent("−", "Remove this pattern"),
                                ToolStyles.Secondary, GUILayout.Width(ToolStyles.IconWidth),
                                GUILayout.Height(ToolStyles.ControlHeight)))
                            removeAt = i;
                    }
                }

                if (settings.droppedFiles.Count == 0)
                    EditorGUILayout.HelpBox("Nothing is dropped — every file in the build folder will be "
                        + "uploaded, .DS_Store included.", MessageType.Warning);

                GUILayout.Space(ToolStyles.SpaceS);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add pattern", ToolStyles.Secondary,
                            GUILayout.Width(ToolStyles.ButtonL), GUILayout.Height(ToolStyles.ControlHeight)))
                    {
                        settings.droppedFiles.Add("");
                        changed = true;
                    }

                    GUILayout.FlexibleSpace();

                    using (new ToolStyles.DisabledScope(IsDefaultDropList()))
                    {
                        if (GUILayout.Button(new GUIContent("Reset to defaults",
                                    string.Join(", ", EasyUploadSettings.DefaultDroppedFiles)),
                                ToolStyles.Secondary, GUILayout.Width(ToolStyles.ButtonL),
                                GUILayout.Height(ToolStyles.ControlHeight)))
                        {
                            settings.droppedFiles = new List<string>(EasyUploadSettings.DefaultDroppedFiles);
                            changed = true;
                        }
                    }
                }

                if (removeAt >= 0)
                {
                    settings.droppedFiles.RemoveAt(removeAt);
                    changed = true;
                }

                if (!changed) return;

                settings.Save();
                // The review on screen was built against the old list, so it no longer says which
                // files would be sent.
                plan = null;
                job = null;
                rowsKey = null;
                if (!string.IsNullOrEmpty(buildPath)) ScanFolder();
            }
        }

        private bool IsDefaultDropList()
        {
            var defaults = EasyUploadSettings.DefaultDroppedFiles;
            if (settings.droppedFiles.Count != defaults.Length) return false;
            for (var i = 0; i < defaults.Length; i++)
                if (settings.droppedFiles[i] != defaults[i]) return false;
            return true;
        }

        // ---------- folder ----------

        /// <summary>
        /// Accepts a folder dragged from Finder/Explorer and one dragged out of the Project window —
        /// Unity reports the second as a path relative to the project, so it needs rooting first.
        /// </summary>
        private void HandleDrop(Rect rect)
        {
            var e = Event.current;

            if (e.type == EventType.DragExited || e.type == EventType.MouseLeaveWindow)
            {
                dragOver = false;
                Repaint();
                return;
            }

            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;

            var inside = rect.Contains(e.mousePosition);
            var folder = inside ? FirstFolder(DragAndDrop.paths) : null;

            if (dragOver != (folder != null))
            {
                dragOver = folder != null;
                Repaint();
            }

            if (!inside) return;

            DragAndDrop.visualMode = folder != null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

            if (e.type == EventType.DragPerform && folder != null)
            {
                DragAndDrop.AcceptDrag();
                dragOver = false;
                SetBuildFolder(folder);
            }
            e.Use();
        }

        private static string FirstFolder(string[] paths)
        {
            if (paths == null) return null;
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var absolute = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
                if (Directory.Exists(absolute)) return Path.GetFullPath(absolute);
            }
            return null;
        }

        private void SetBuildFolder(string path)
        {
            buildPath = path;
            planError = "";
            plan = null;
            job = null;
            lastFolderProbe = double.NegativeInfinity;   // the folder just changed; do not wait to notice
            folderFiles = -1;
            folderBytes = 0;
            settings.lastBuildPath = path;
            settings.Save();
            if (!string.IsNullOrEmpty(path)) ScanFolder();
            Repaint();
        }

        private string FolderSummary()
        {
            if (scanningFolder || folderFiles < 0) return "Reading the folder…";
            return folderFiles + (folderFiles == 1 ? " file · " : " files · ") +
                   UploadPlanner.HumanBytes(folderBytes);
        }

        /// <summary>
        /// The file count and size, so the card says something concrete about the folder before the
        /// review runs. Off the main thread — a build folder can hold thousands of files.
        /// </summary>
        private void ScanFolder()
        {
            var root = buildPath;
            var patterns = new List<string>(settings.droppedFiles);
            scanningFolder = true;

            RunInBackground(() =>
            {
                var count = 0;
                long bytes = 0;
                try
                {
                    foreach (var file in UploadPlanner.Walk(root, patterns, CancellationToken.None))
                    {
                        if (file.Junk) continue;
                        count++;
                        bytes += file.Size;
                    }
                }
                catch (Exception) { count = -1; }

                Post(() =>
                {
                    if (buildPath != root) return;   // the user moved on while we were counting
                    folderFiles = count;
                    folderBytes = bytes;
                    scanningFolder = false;
                    Repaint();
                });
            });
        }

        private static string Shorten(string path, int max)
        {
            if (path.Length <= max) return path;
            return "…" + path.Substring(path.Length - (max - 1));
        }

        private static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + "h ago";
            return (int)span.TotalDays + "d ago";
        }

        // ---------- actions ----------

        private S3Client NewClient() => new S3Client(credentials, settings.endpoint, settings.region);

        private void Connect()
        {
            var parsed = AwsCredentials.Parse(pasteText, out var error);
            if (parsed == null)
            {
                connectionOk = false;
                connectionMessage = error;
                return;
            }

            connecting = true;
            connectionMessage = "Checking with AWS…";
            var client = new S3Client(parsed, settings.endpoint, settings.region);

            RunInBackground(() =>
            {
                try
                {
                    // Checked before it is stored, so a wrong or already-expired paste is caught here
                    // rather than halfway through a deploy.
                    var who = client.CheckCredentials();
                    Post(() =>
                    {
                        credentials = parsed;
                        connectionOk = true;
                        connecting = false;
                        connectionMessage = "";
                        connectionIdentity = who;
                        pasteText = "";
                        if (settings.rememberCredentials) AwsCredentials.Save(credentials);
                        RefreshBuckets();
                        tab = Tab.Deploy;
                        Repaint();
                    });
                }
                catch (Exception e)
                {
                    Post(() =>
                    {
                        connecting = false;
                        connectionOk = false;
                        connectionMessage = e.Message;
                        Repaint();
                    });
                }
            });
        }

        private void VerifyConnection()
        {
            if (credentials == null) return;

            connecting = true;
            var client = NewClient();

            RunInBackground(() =>
            {
                try
                {
                    var who = client.CheckCredentials();
                    Post(() =>
                    {
                        connecting = false;
                        connectionOk = true;
                        connectionMessage = "";
                        connectionIdentity = who;
                        if (allBuckets.Count == 0) RefreshBuckets();
                        Repaint();
                    });
                }
                catch (Exception e)
                {
                    Post(() =>
                    {
                        connecting = false;
                        connectionOk = false;
                        connectionMessage = e.Message;
                        Repaint();
                    });
                }
            });
        }

        private void RefreshBuckets()
        {
            if (credentials == null || loadingBuckets) return;

            loadingBuckets = true;
            bucketError = "";
            var client = NewClient();

            RunInBackground(() =>
            {
                try
                {
                    var buckets = client.ListBuckets();
                    Post(() =>
                    {
                        allBuckets = buckets;
                        loadingBuckets = false;
                        Repaint();
                    });
                }
                catch (Exception e)
                {
                    Post(() =>
                    {
                        loadingBuckets = false;
                        bucketError = e.Message;
                        Repaint();
                    });
                }
            });
        }

        private void StartReview()
        {
            planError = "";
            planning = true;
            planStatus = "Starting…";

            planCancellation?.Cancel();
            planCancellation = new CancellationTokenSource();
            var token = planCancellation.Token;

            var client = NewClient();
            var root = buildPath;
            var buckets = new List<string>(settings.buckets);
            var version = settings.version;
            var dropPatterns = new List<string>(settings.droppedFiles);

            RunInBackground(() =>
            {
                try
                {
                    var built = UploadPlanner.Build(client, root, buckets, version, false, dropPatterns, token,
                        status => Post(() => { planStatus = status; Repaint(); }));

                    Post(() =>
                    {
                        planning = false;
                        plan = built;
                        job = null;
                        rowsKey = null;
                        listScroll = Vector2.zero;
                        Repaint();
                    });
                }
                catch (OperationCanceledException)
                {
                    Post(() => { planning = false; planStatus = ""; Repaint(); });
                }
                catch (Exception e)
                {
                    Post(() =>
                    {
                        planning = false;
                        planError = e.Message;
                        Repaint();
                    });
                }
            });
        }

        private static void RunInBackground(Action work)
        {
            var thread = new Thread(() => work()) { IsBackground = true, Name = "EasyUpload" };
            thread.Start();
        }
    }
}
