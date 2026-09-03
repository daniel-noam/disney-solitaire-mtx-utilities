using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Utilities.Editor.EasyUpload
{
    /// <summary>
    /// Everything the window remembers except the credentials, which live in the file the desktop
    /// app owns (see <see cref="AwsCredentials.StorePath"/>) and are never written here.
    ///
    /// Kept in ProjectSettings/ rather than a user folder because the interesting values — which
    /// buckets this project deploys to, which version folder — belong to the project, not the
    /// machine.
    /// </summary>
    [Serializable]
    public class EasyUploadSettings
    {
        private const string ProjectRelativeSettingsPath = "ProjectSettings/EasyUploadSettings.json";

        /// <summary>
        /// ProjectSettings/ is committed, so this file would otherwise ride into the shared repo and
        /// hand everyone else this machine's bucket selection and build path. The credentials are
        /// not listed because they never go in the project at all — they live in the desktop app's
        /// config folder (see <see cref="AwsCredentials.StorePath"/>).
        /// </summary>
        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            yield return ProjectRelativeSettingsPath;
        }

        /// <summary>Key prefix inside each bucket, e.g. "V4" → s3://bucket/V4/...</summary>
        public string version = "V4";

        /// <summary>Bucket names selected as upload targets.</summary>
        public List<string> buckets = new List<string>();

        /// <summary>
        /// Buckets pinned to the top of the list. Independent of <see cref="buckets"/>: a pin is
        /// "one I look at often", a target is "one this deploy goes to".
        /// </summary>
        public List<string> favorites = new List<string>();

        /// <summary>Region used to discover buckets. Per-bucket regions are resolved at upload time.</summary>
        public string region = "us-east-1";

        /// <summary>Non-empty points S3 at something other than AWS (a local MinIO), for testing.</summary>
        public string endpoint = "";

        /// <summary>Opened by the "Open AWS portal" button beside the paste box.</summary>
        public string portalUrl = "https://d-9067910cba.awsapps.com/start/#/";

        /// <summary>
        /// Keep credentials on disk between launches. Off keeps them in memory for the session and
        /// writes nothing — right on a shared or backed-up machine, at the cost of a paste per launch.
        /// </summary>
        public bool rememberCredentials = true;

        /// <summary>Uploads in flight. Game assets are small, so this is round trips, not bandwidth.</summary>
        public int concurrency = 24;

        /// <summary>
        /// File names that are walked and listed but never uploaded.
        ///
        /// Matched on the file name alone, so an entry applies at every depth. `*` and `?` work, so
        /// `._*` covers the AppleDouble files a copy to a non-Mac volume leaves behind.
        ///
        /// The three defaults are what the EasyUpload desktop app drops, so an untouched list keeps
        /// both tools sending exactly the same set of files.
        /// </summary>
        public List<string> droppedFiles = new List<string>(DefaultDroppedFiles);

        public static readonly string[] DefaultDroppedFiles = { ".DS_Store", "Thumbs.db", "desktop.ini" };

        /// <summary>
        /// Bumped when a stored default changes in a way an existing file should pick up. Only used
        /// by <see cref="Migrate"/>.
        /// </summary>
        public int schema;

        /// <summary>The last folder that was dropped, so reopening the window does not start blank.</summary>
        public string lastBuildPath = "";

        /// <summary>
        /// Asset folders whose templates get a description JSON.
        ///
        /// A folder list rather than a component check, because the component cannot tell them
        /// apart: a badge carries the same DynamicTemplate a popup does, and only the folder it
        /// lives in says that a badge is configured without one. Editable because that split is a
        /// team convention, not something the project states anywhere — a new kind of template
        /// should be a line here, not a code change.
        ///
        /// Matched as path prefixes, so a folder covers everything nested under it.
        /// </summary>
        public List<string> jsonFolders = new List<string>(DefaultJsonFolders);

        public static readonly string[] DefaultJsonFolders =
        {
            "Assets/Export/Templates",
            "Assets/Export/DynamicTemplateTooltips",
            "Assets/Export/AreYouSureTemplates",
        };

        /// <summary>
        /// Where the config JSONs are written — the campaign's folder on the desktop, beside its
        /// build folders.
        ///
        /// Remembered because it is chosen once per campaign and then written into every build,
        /// which is also why it is the one thing here worth a second look when campaigns change:
        /// nothing in a build says which campaign folder it belongs to.
        /// </summary>
        public string lastJsonPath = "";

        public static readonly string[] Versions =
        {
            "V1", "V2", "V3", "V4", "V5", "V6", "V7", "V8", "V9", "V10",
        };

        private static EasyUploadSettings instance;

        public static EasyUploadSettings Instance
        {
            get
            {
                if (instance == null) instance = Load();
                return instance;
            }
        }

        private static string SettingsPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", ProjectRelativeSettingsPath);

        private static EasyUploadSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonUtility.FromJson<EasyUploadSettings>(File.ReadAllText(SettingsPath));
                    if (loaded != null) return loaded.Migrate().Normalised();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EasyUpload] Could not read settings, starting from defaults: " + e.Message);
            }
            return new EasyUploadSettings();
        }

        /// <summary>
        /// Carries an existing file forward when a default changes. The first version shipped with
        /// 8 uploads in flight, which is a third of what the desktop app uses and was the reason a
        /// deploy from Unity felt slower; nobody would think to go and change it by hand.
        /// </summary>
        private EasyUploadSettings Migrate()
        {
            if (schema < 1)
            {
                if (concurrency <= 8) concurrency = 24;
                schema = 1;
            }
            if (schema < 2)
            {
                // A file written before the list existed has nothing to say about it, which is not
                // the same as saying "drop nothing" — so it gets the defaults rather than an empty
                // list that would start uploading .DS_Store.
                if (droppedFiles == null || droppedFiles.Count == 0)
                    droppedFiles = new List<string>(DefaultDroppedFiles);
                schema = 2;
            }
            if (schema < 3)
            {
                // Same reasoning as droppedFiles: a file written before the list existed says
                // nothing about it, which is not the same as saying "no folder qualifies".
                if (jsonFolders == null || jsonFolders.Count == 0)
                    jsonFolders = new List<string>(DefaultJsonFolders);
                schema = 3;
            }
            return this;
        }

        public void Save()
        {
            try
            {
                Normalised();
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(this, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EasyUpload] Could not save settings: " + e.Message);
            }
        }

        /// <summary>
        /// A hand-edited or older file can arrive with blanks and duplicates. Fixing them on load
        /// keeps the rest of the code from having to allow for it.
        /// </summary>
        private EasyUploadSettings Normalised()
        {
            if (string.IsNullOrWhiteSpace(version)) version = "V4";
            if (string.IsNullOrWhiteSpace(region)) region = "us-east-1";
            if (string.IsNullOrWhiteSpace(portalUrl)) portalUrl = new EasyUploadSettings().portalUrl;
            endpoint = (endpoint ?? "").Trim().TrimEnd('/');
            concurrency = Mathf.Clamp(concurrency, 1, 32);

            buckets = Deduped(buckets);
            favorites = Deduped(favorites);
            // Not defaulted when empty: an empty list is a legitimate choice meaning "upload
            // everything", and only Migrate may put the defaults back.
            droppedFiles = Deduped(droppedFiles);

            // Trailing slashes and backslashes would both defeat the prefix match, and a
            // hand-edited file is exactly where they turn up.
            jsonFolders = Deduped(jsonFolders);
            for (var i = 0; i < jsonFolders.Count; i++)
                jsonFolders[i] = jsonFolders[i].Replace('\\', '/').TrimEnd('/');

            return this;
        }

        private static List<string> Deduped(List<string> source)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            if (source == null) return result;
            foreach (var raw in source)
            {
                var value = (raw ?? "").Trim();
                if (value.Length > 0 && seen.Add(value)) result.Add(value);
            }
            return result;
        }

        public bool IsFavorite(string bucket) => favorites.Contains(bucket);

        public void ToggleFavorite(string bucket)
        {
            if (!favorites.Remove(bucket)) favorites.Add(bucket);
            Save();
        }
    }
}
