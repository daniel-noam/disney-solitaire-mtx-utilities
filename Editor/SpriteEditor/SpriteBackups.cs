using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Copies of source art taken before a tool overwrites it, with the import settings that would
    /// otherwise be lost with it.
    ///
    /// Backups live next to Assets/ rather than inside it: the first version of these tools wrote
    /// "name.original.png" beside the asset, which Unity then imported as a second copy of every
    /// texture it touched.
    /// </summary>
    public static class SpriteBackups
    {
        private const string FolderName = "SpriteEditorBackups";

        /// <summary>Where the 9-slice tool alone used to put them, still read so old backups restore.</summary>
        private const string LegacyFolderName = "NineSliceBackups";

        private const string ManifestFileName = "manifest.json";

        private static string Root => Path.Combine(SpriteImage.ProjectRoot, FolderName);
        private static string LegacyRoot => Path.Combine(SpriteImage.ProjectRoot, LegacyFolderName);
        private static string ManifestPath => Path.Combine(Root, ManifestFileName);
        private static string LegacyManifestPath => Path.Combine(LegacyRoot, ManifestFileName);

        /// <summary>Name to show when telling someone where their original went.</summary>
        public static string FolderLabel => FolderName + "/";

        /// <summary>
        /// These sit beside Assets/ at the project root, where nothing in a standard Unity .gitignore
        /// covers them. The legacy folder is listed too - old backups still on disk are just as visible.
        /// </summary>
        [GitExcludeProvider]
        private static IEnumerable<string> GitExcludePaths()
        {
            yield return FolderName;
            yield return LegacyFolderName;
        }

        public static bool Has(string assetPath)
        {
            return FindEntry(LoadManifest(), assetPath) != null;
        }

        /// <summary>
        /// Copies the file and records the import settings a tool is about to change. Keeps the
        /// oldest copy: after a second pass the current file is already modified, so overwriting the
        /// backup would lose the only copy of the original art.
        /// </summary>
        public static bool Save(string assetPath, TextureImporter importer, out string error)
        {
            error = null;
            try
            {
                string backupPath = Path.Combine(Root, assetPath);
                string backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDirectory)) Directory.CreateDirectory(backupDirectory);

                if (!File.Exists(backupPath))
                    File.Copy(SpriteImage.ToAbsolutePath(assetPath), backupPath);

                var manifest = LoadManifest();
                if (FindEntry(manifest, assetPath) == null)
                {
                    manifest.entries.Add(new BackupEntry
                    {
                        assetPath = assetPath,
                        originalBorder = importer.spriteBorder,
                        originalTextureType = importer.textureType.ToString(),
                        originalPivot = importer.spritePivot,
                        originalAlignment = ReadAlignment(importer),
                        savedUtc = DateTime.UtcNow.ToString("u"),
                    });

                    SaveManifest(manifest);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = $"backup failed: {exception.Message}";
                return false;
            }
        }

        /// <summary>Puts the file and its import settings back, then drops the backup.</summary>
        public static bool Restore(string assetPath, out string message)
        {
            var manifest = LoadManifest();
            var entry = FindEntry(manifest, assetPath);
            if (entry == null)
            {
                message = "no backup recorded";
                return false;
            }

            var notes = new List<string>();
            try
            {
                string backupPath = Path.Combine(Root, assetPath);
                if (!File.Exists(backupPath)) backupPath = Path.Combine(LegacyRoot, assetPath);

                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, SpriteImage.ToAbsolutePath(assetPath), true);
                    File.Delete(backupPath);
                    notes.Add("pixels restored");
                }
                else
                {
                    notes.Add("backup file missing, import settings only");
                }

                if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
                {
                    importer.spriteBorder = entry.originalBorder;
                    if (Enum.TryParse(entry.originalTextureType, out TextureImporterType textureType))
                        importer.textureType = textureType;

                    WritePivot(importer, entry.originalPivot, entry.originalAlignment);
                    importer.SaveAndReimport();
                    notes.Add($"border {NineSliceBorder.FromVector4(entry.originalBorder)}");
                }

                manifest.entries.Remove(entry);
                SaveManifest(manifest);
            }
            catch (Exception exception)
            {
                message = $"restore failed: {exception.Message}";
                return false;
            }

            message = string.Join(", ", notes);
            return true;
        }

        // -------------------------------------------------------------------------------------------
        // Pivot, which only TextureImporterSettings exposes properly
        // -------------------------------------------------------------------------------------------

        public static int ReadAlignment(TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            return settings.spriteAlignment;
        }

        /// <summary>
        /// Writes a pivot. It has to go through TextureImporterSettings rather than
        /// <see cref="TextureImporter.spritePivot"/>, which is ignored unless the alignment says
        /// Custom - the usual reason a "moved" pivot silently does nothing.
        /// </summary>
        public static void WritePivot(TextureImporter importer, Vector2 pivot, int alignment)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteAlignment = alignment;
            settings.spritePivot = pivot;
            importer.SetTextureSettings(settings);
        }

        // -------------------------------------------------------------------------------------------
        // Manifest
        // -------------------------------------------------------------------------------------------

        [Serializable]
        private class BackupEntry
        {
            public string assetPath;
            public Vector4 originalBorder;
            public string originalTextureType;
            public Vector2 originalPivot;
            public int originalAlignment;
            public string savedUtc;
        }

        [Serializable]
        private class BackupManifest
        {
            public List<BackupEntry> entries = new List<BackupEntry>();
        }

        private static BackupEntry FindEntry(BackupManifest manifest, string assetPath)
        {
            for (int i = 0; i < manifest.entries.Count; i++)
                if (manifest.entries[i].assetPath == assetPath) return manifest.entries[i];
            return null;
        }

        private static BackupManifest LoadManifest()
        {
            // The legacy manifest is only read when there is no current one, so a project that has
            // used the new tools carries on from theirs.
            string path = File.Exists(ManifestPath) ? ManifestPath : LegacyManifestPath;

            try
            {
                if (File.Exists(path))
                {
                    var manifest = JsonUtility.FromJson<BackupManifest>(File.ReadAllText(path));
                    if (manifest?.entries != null) return manifest;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{SpriteImage.Log} Could not read '{path}': {exception.Message}");
            }

            return new BackupManifest();
        }

        private static void SaveManifest(BackupManifest manifest)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(ManifestPath, JsonUtility.ToJson(manifest, true));
            }
            catch (Exception exception)
            {
                Debug.LogError($"{SpriteImage.Log} Could not write '{ManifestPath}': {exception.Message}");
            }
        }
    }
}
