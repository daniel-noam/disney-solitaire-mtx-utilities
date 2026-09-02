using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// Writes 9-slice borders onto texture importers, optionally collapsing the image file itself,
    /// and can put a compressed file back the way it was.
    ///
    /// Setting a border is non-destructive and reversible through the importer alone. Collapsing the
    /// pixels is not, so that path always goes through <see cref="SpriteBackups"/> first.
    /// </summary>
    public static class NineSliceApplier
    {
        // ---------------------------------------------------------------------------------------
        // Queries
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Whether a single spriteBorder is meaningful for this asset. External files have no
        /// importer to gate on - the pixel cut works on any of them, so nothing here blocks it.
        /// </summary>
        public static bool CanSlice(SpriteTarget target, out string reason)
        {
            reason = null;
            return target.IsExternal || CanSlice(target.assetPath, out reason);
        }

        /// <summary>Whether a single spriteBorder is meaningful for this asset.</summary>
        public static bool CanSlice(string assetPath, out string reason)
        {
            reason = null;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                reason = "Not imported as a texture.";
                return false;
            }

            switch (importer.spriteImportMode)
            {
                case SpriteImportMode.Multiple:
                    // Each sub-sprite carries its own border in the sprite sheet metadata, so a
                    // single importer-level border would be ignored.
                    reason = "Sprite Mode is Multiple - borders belong to each sub-sprite, edit them in the Sprite Editor.";
                    return false;
                case SpriteImportMode.Polygon:
                    reason = "Sprite Mode is Polygon, which has no 9-slice border.";
                    return false;
                default:
                    return true;
            }
        }

        public static NineSliceBorder ReadBorder(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return importer == null ? NineSliceBorder.Zero : NineSliceBorder.FromVector4(importer.spriteBorder);
        }

        public static TextureImporterType ReadTextureType(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return importer == null ? TextureImporterType.Default : importer.textureType;
        }

        public static bool HasBackup(string assetPath)
        {
            return SpriteBackups.Has(assetPath);
        }

        // ---------------------------------------------------------------------------------------
        // Apply
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Applies to a file that may be outside the project. A project asset goes through the
        /// normal border-and-cut path below; an external one has no importer to put a border on, so
        /// only the pixel cut applies to it.
        /// </summary>
        public static bool Apply(SpriteTarget target, NineSliceBorder border, NineSliceOptions options,
            out string message)
        {
            return target.IsExternal
                ? ApplyExternal(target, border, options, out message)
                : Apply(target.assetPath, border, options, out message);
        }

        /// <summary>
        /// Cuts the stretchable centre out of a file outside the project. There is no importer to
        /// put a border on and no backup mechanism without one, so Border Only and the backup/restore
        /// flow both stay out of reach here - only the pixel cut applies.
        /// </summary>
        private static bool ApplyExternal(SpriteTarget target, NineSliceBorder border, NineSliceOptions options,
            out string message)
        {
            options.Validate();
            var notes = new List<string>();

            if (options.borderOnly)
            {
                message = "This file is outside the project, so there is no importer to store a border " +
                          "on. Turn off Border Only to cut the stretchable centre instead.";
                return false;
            }

            if (!CanCutPixels(target.absolutePath, border, notes))
            {
                message = string.Join(", ", notes);
                return false;
            }

            using (var snapshot = SpriteImage.LoadFile(target.absolutePath, out string loadError))
            {
                if (snapshot == null)
                {
                    message = $"not cut ({loadError})";
                    return false;
                }

                var predicted = NineSliceAnalyzer.PredictCompressedSize(
                    snapshot.Width, snapshot.Height, border, options.centerSize);
                if (predicted.x == snapshot.Width && predicted.y == snapshot.Height)
                {
                    message = "already at or below the target centre size";
                    return false;
                }

                if (!TryEncodeCut(snapshot, target.absolutePath, border, options, notes, out byte[] bytes))
                {
                    message = string.Join(", ", notes);
                    return false;
                }

                string destination = options.TargetsOriginal
                    ? target.absolutePath
                    : target.SiblingAbsolutePath(NineSliceOptions.SanitizeSuffix(options.newFileSuffix),
                        Path.GetExtension(target.absolutePath));

                try
                {
                    File.WriteAllBytes(destination, bytes);
                }
                catch (Exception exception)
                {
                    message = $"no file written ({exception.Message})";
                    return false;
                }

                notes.Add($"cut {snapshot.Width}x{snapshot.Height} to {predicted.x}x{predicted.y}");
                notes.Add(options.TargetsOriginal
                    ? "written outside the project, so nothing was imported and there is no backup"
                    : $"wrote {Path.GetFileName(destination)}, original untouched");
                message = string.Join(", ", notes);
                return true;
            }
        }

        /// <summary>
        /// Applies the border, and cuts pixels when asked to. Two independent choices drive this:
        ///
        /// <list type="bullet">
        /// <item>Where it acts - the original asset, or a new sibling file next to it. Everything
        /// happens to that one asset; the other is left completely alone.</item>
        /// <item>Whether pixels are cut - the stretchable centre collapsed, or the image left as is.</item>
        /// </list>
        ///
        /// <paramref name="message"/> always describes what happened, including skipped steps.
        /// </summary>
        public static bool Apply(string assetPath, NineSliceBorder border, NineSliceOptions options, out string message)
        {
            options.Validate();
            var notes = new List<string>();

            if (!CanSlice(assetPath, out string reason))
            {
                message = reason;
                return false;
            }

            return options.TargetsOriginal
                ? ApplyToOriginal(assetPath, border, options, notes, out message)
                : ApplyToSibling(assetPath, border, options, notes, out message);
        }

        private static bool ApplyToOriginal(string assetPath, NineSliceBorder border, NineSliceOptions options,
            List<string> notes, out string message)
        {
            var importer = (TextureImporter) AssetImporter.GetAtPath(assetPath);

            // A failed or skipped cut is not fatal - it explains itself in notes, and the border is
            // still worth writing.
            if (options.CutsPixels) TryCutIntoOriginal(assetPath, importer, border, options, notes);
            else notes.Add("image file unchanged");

            if (!TryWriteBorder(importer, border, notes, out string borderError))
            {
                message = borderError;
                return false;
            }

            notes.Insert(0, $"border {border}");
            message = string.Join(", ", notes);
            return true;
        }

        /// <summary>
        /// Writes a new file beside the original - cut down, or a verbatim copy when only the border
        /// is wanted - and puts the border on that file. The original is not touched at all, not even
        /// its import settings.
        /// </summary>
        private static bool ApplyToSibling(string assetPath, NineSliceBorder border, NineSliceOptions options,
            List<string> notes, out string message)
        {
            if (!TryCreateSibling(assetPath, border, options, notes, out string createdPath))
            {
                message = string.Join(", ", notes);
                return false;
            }

            AssetDatabase.ImportAsset(createdPath, ImportAssetOptions.ForceUpdate);
            if (!(AssetImporter.GetAtPath(createdPath) is TextureImporter created))
            {
                message = $"Wrote {Path.GetFileName(createdPath)} but Unity did not import it as a texture.";
                return false;
            }

            if (!TryWriteBorder(created, border, notes, out string borderError))
            {
                message = borderError;
                return false;
            }

            notes.Insert(0, $"border {border} on {Path.GetFileName(createdPath)}");
            notes.Add("original untouched");
            message = string.Join(", ", notes);
            return true;
        }

        /// <summary>
        /// Makes the importer a Single-mode sprite and writes the border.
        /// <paramref name="notes"/> may be null to keep the running commentary to one file.
        /// </summary>
        private static bool TryWriteBorder(TextureImporter importer, NineSliceBorder border, List<string> notes,
            out string error)
        {
            error = null;
            try
            {
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    notes?.Add("texture type set to Sprite");
                }

                // spriteBorder is only honoured in Single mode.
                if (importer.spriteImportMode != SpriteImportMode.Single)
                    importer.spriteImportMode = SpriteImportMode.Single;

                importer.spriteBorder = border.ToVector4();
                importer.SaveAndReimport();
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not write import settings: {exception.Message}";
                return false;
            }
        }

        /// <summary>Clears the border without touching the pixels or the texture type.</summary>
        public static bool ClearBorder(string assetPath, out string message)
        {
            if (!CanSlice(assetPath, out string reason))
            {
                message = reason;
                return false;
            }

            var importer = (TextureImporter) AssetImporter.GetAtPath(assetPath);
            try
            {
                importer.spriteBorder = Vector4.zero;
                importer.SaveAndReimport();
            }
            catch (Exception exception)
            {
                message = $"Could not write import settings: {exception.Message}";
                return false;
            }

            message = "border cleared";
            return true;
        }

        /// <summary>Overwrites the original image with its centre cut down, backing it up first.</summary>
        private static bool TryCutIntoOriginal(string assetPath, TextureImporter importer, NineSliceBorder border,
            NineSliceOptions options, List<string> notes)
        {
            if (!CanCutPixels(assetPath, border, notes)) return false;

            using (var snapshot = SpriteImage.Load(assetPath, out string loadError))
            {
                if (snapshot == null)
                {
                    notes.Add($"not cut ({loadError})");
                    return false;
                }

                // Would mean overwriting the source file with imported - possibly downscaled - pixels.
                if (!snapshot.IsSourceResolution)
                {
                    notes.Add("not cut (could not read the file at source resolution)");
                    return false;
                }

                var predicted = NineSliceAnalyzer.PredictCompressedSize(
                    snapshot.Width, snapshot.Height, border, options.centerSize);
                if (predicted.x == snapshot.Width && predicted.y == snapshot.Height)
                {
                    notes.Add("already at or below the target centre size");
                    return false;
                }

                if (!TryEncodeCut(snapshot, assetPath, border, options, notes, out byte[] bytes)) return false;

                try
                {
                    if (options.createBackup && !SpriteBackups.Save(assetPath, importer, out string backupError))
                    {
                        // Refuse to overwrite art we could not back up first.
                        notes.Add($"not cut ({backupError})");
                        return false;
                    }

                    File.WriteAllBytes(SpriteImage.ToAbsolutePath(assetPath), bytes);
                    notes.Add($"cut {snapshot.Width}x{snapshot.Height} to {predicted.x}x{predicted.y}");
                    return true;
                }
                catch (Exception exception)
                {
                    notes.Add($"not cut ({exception.Message})");
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes the sibling file. Cut down when the options say so, otherwise a byte-for-byte copy -
        /// which works for any image format, since nothing is decoded.
        /// </summary>
        private static bool TryCreateSibling(string assetPath, NineSliceBorder border, NineSliceOptions options,
            List<string> notes, out string createdPath)
        {
            createdPath = null;

            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                notes.Add("no file written (only assets under Assets/ can be copied)");
                return false;
            }

            string destination = NineSliceOptions.BuildNewAssetPath(assetPath, options.newFileSuffix);

            // SanitizeSuffix should make this impossible; checked anyway, because getting it wrong
            // means overwriting the original in the mode that promises not to touch it.
            if (destination == assetPath)
            {
                notes.Add("no file written (the new file name would collide with the original)");
                return false;
            }

            byte[] bytes;
            if (options.CutsPixels)
            {
                if (!CanCutPixels(assetPath, border, notes)) return false;

                using (var snapshot = SpriteImage.Load(assetPath, out string loadError))
                {
                    if (snapshot == null)
                    {
                        notes.Add($"no file written ({loadError})");
                        return false;
                    }

                    if (!snapshot.IsSourceResolution)
                    {
                        notes.Add("no file written (could not read the file at source resolution)");
                        return false;
                    }

                    if (!TryEncodeCut(snapshot, assetPath, border, options, notes, out bytes)) return false;

                    var predicted = NineSliceAnalyzer.PredictCompressedSize(
                        snapshot.Width, snapshot.Height, border, options.centerSize);
                    notes.Add(predicted.x == snapshot.Width && predicted.y == snapshot.Height
                        ? $"nothing to cut, {Path.GetFileName(destination)} is the same size"
                        : $"cut {snapshot.Width}x{snapshot.Height} to {predicted.x}x{predicted.y}");
                }
            }
            else
            {
                try
                {
                    bytes = File.ReadAllBytes(SpriteImage.ToAbsolutePath(assetPath));
                }
                catch (Exception exception)
                {
                    notes.Add($"no file written ({exception.Message})");
                    return false;
                }

                notes.Add("image copied unchanged");
            }

            try
            {
                File.WriteAllBytes(SpriteImage.ToAbsolutePath(destination), bytes);
            }
            catch (Exception exception)
            {
                notes.Add($"no file written ({exception.Message})");
                return false;
            }

            createdPath = destination;
            return true;
        }

        /// <summary>Guards shared by both cutting paths.</summary>
        private static bool CanCutPixels(string assetPath, NineSliceBorder border, List<string> notes)
        {
            if (!SpriteImage.IsRewritableImage(assetPath))
            {
                notes.Add($"not cut ('{Path.GetExtension(assetPath)}' cannot be re-encoded)");
                return false;
            }

            if (border.IsZero)
            {
                notes.Add("not cut (no border, so there is no redundant centre to remove)");
                return false;
            }

            return true;
        }

        private static bool TryEncodeCut(SpriteSnapshot snapshot, string assetPath, NineSliceBorder border,
            NineSliceOptions options, List<string> notes, out byte[] bytes)
        {
            bytes = null;
            var cut = NineSliceAnalyzer.CreateCompressed(snapshot, border, options.centerSize);
            try
            {
                bool png = Path.GetExtension(assetPath).ToLowerInvariant() == ".png";
                bytes = png ? cut.EncodeToPNG() : cut.EncodeToJPG(options.jpgQuality);
                if (bytes != null && bytes.Length > 0) return true;

                notes.Add("not cut (encoding produced no data)");
                return false;
            }
            catch (Exception exception)
            {
                notes.Add($"not cut ({exception.Message})");
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cut);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Backup / restore
        // ---------------------------------------------------------------------------------------

        /// <summary>Puts the original file, border and texture type back, then drops the backup.</summary>
        public static bool Restore(string assetPath, out string message)
        {
            return SpriteBackups.Restore(assetPath, out message);
        }
    }
}
