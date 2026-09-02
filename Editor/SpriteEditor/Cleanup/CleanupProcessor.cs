using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Fixes what surrounds the art without touching the art itself: bleeds colour out behind the
    /// transparent pixels, crops the empty margin, rounds the canvas up to a power of two.
    ///
    /// Every visible pixel comes through unchanged, which is what lets this run over finished art.
    /// The geometry can move, so the pivot and the 9-slice border are recalculated to match rather
    /// than left to drift.
    /// </summary>
    public static class CleanupProcessor
    {
        // -------------------------------------------------------------------------------------------
        // Geometry
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// What the pass would do to the image's size, without touching a pixel. Cheap enough to
        /// call while drawing the settings.
        /// </summary>
        public static CleanupPlan Plan(SpriteSnapshot snapshot, CleanupOptions options)
        {
            options.Validate();

            var plan = new CleanupPlan
            {
                Crop = new RectInt(0, 0, snapshot.Width, snapshot.Height),
                OutputWidth = snapshot.Width,
                OutputHeight = snapshot.Height,
            };

            if (options.trim && TryFindContent(snapshot, options.trimAlpha, out var content))
            {
                // The margin is grown before clamping, so asking to keep a few pixels of empty
                // space never reaches outside the image.
                int margin = options.trimMargin;
                int xMin = Mathf.Max(0, content.xMin - margin);
                int yMin = Mathf.Max(0, content.yMin - margin);
                int xMax = Mathf.Min(snapshot.Width, content.xMax + margin);
                int yMax = Mathf.Min(snapshot.Height, content.yMax + margin);
                plan.Crop = new RectInt(xMin, yMin, Mathf.Max(1, xMax - xMin), Mathf.Max(1, yMax - yMin));
            }

            plan.OutputWidth = plan.Crop.width;
            plan.OutputHeight = plan.Crop.height;

            switch (options.padding)
            {
                case CleanupPadding.PowerOfTwo:
                    plan.OutputWidth = Mathf.NextPowerOfTwo(plan.Crop.width);
                    plan.OutputHeight = Mathf.NextPowerOfTwo(plan.Crop.height);
                    break;

                case CleanupPadding.SquarePowerOfTwo:
                    int side = Mathf.NextPowerOfTwo(Mathf.Max(plan.Crop.width, plan.Crop.height));
                    plan.OutputWidth = side;
                    plan.OutputHeight = side;
                    break;

                case CleanupPadding.MultipleOfFour:
                    plan.OutputWidth = RoundUpToMultiple(plan.Crop.width, 4);
                    plan.OutputHeight = RoundUpToMultiple(plan.Crop.height, 4);
                    break;
            }

            if (options.anchor == CleanupAnchor.Center)
            {
                plan.OffsetX = (plan.OutputWidth - plan.Crop.width) / 2;
                plan.OffsetY = (plan.OutputHeight - plan.Crop.height) / 2;
            }

            plan.ChangesGeometry = plan.Crop.x != 0 || plan.Crop.y != 0 ||
                                   plan.OutputWidth != snapshot.Width || plan.OutputHeight != snapshot.Height;
            return plan;
        }

        private static int RoundUpToMultiple(int value, int multiple)
        {
            return (value + multiple - 1) / multiple * multiple;
        }

        /// <summary>
        /// Bounding box of everything at or above <paramref name="alphaCutoff"/>. False when the
        /// image is empty, in which case cropping it would leave nothing to crop to.
        /// </summary>
        private static bool TryFindContent(SpriteSnapshot snapshot, int alphaCutoff, out RectInt content)
        {
            var pixels = snapshot.Pixels;
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;

            for (int y = 0; y < snapshot.Height; y++)
            {
                int row = y * snapshot.Width;
                for (int x = 0; x < snapshot.Width; x++)
                {
                    if (pixels[row + x].a < alphaCutoff) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (minX > maxX)
            {
                content = default;
                return false;
            }

            content = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        // -------------------------------------------------------------------------------------------
        // Pixels
        // -------------------------------------------------------------------------------------------

        /// <summary>The cleaned image, laid out according to <paramref name="plan"/>.</summary>
        public static Color32[] BuildPixels(SpriteSnapshot snapshot, CleanupOptions options, CleanupPlan plan)
        {
            var output = new Color32[plan.OutputWidth * plan.OutputHeight];
            var source = snapshot.Pixels;

            for (int y = 0; y < plan.Crop.height; y++)
            {
                int sourceRow = (plan.Crop.yMin + y) * snapshot.Width + plan.Crop.xMin;
                int targetRow = (plan.OffsetY + y) * plan.OutputWidth + plan.OffsetX;
                Array.Copy(source, sourceRow, output, targetRow, plan.Crop.width);
            }

            // Bleeding comes last, so freshly added padding is filled too - it is exactly the region
            // filtering will sample when the sprite is scaled.
            if (options.bleed) Bleed(output, plan.OutputWidth, plan.OutputHeight, options.bleedPasses);
            return output;
        }

        /// <summary>
        /// Pushes visible colour outwards into transparent pixels, one ring per pass, leaving every
        /// alpha value exactly as it was.
        ///
        /// Only the ring itself is walked each pass, not the whole image: on a 2K texture the naive
        /// version costs a hundred million neighbour reads and turns a live preview into a slideshow.
        /// Each pass also reads the state at its start, so colour spreads evenly rather than racing
        /// along whichever direction the loop happens to run.
        /// </summary>
        private static void Bleed(Color32[] pixels, int width, int height, int passes)
        {
            var filled = new bool[pixels.Length];
            var queued = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) filled[i] = pixels[i].a > 0;

            // One full scan to find the pixels touching the visible art; everything after this
            // works outwards from what the previous pass filled.
            var frontier = new List<int>();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!filled[i]) continue;
                QueueNeighbours(i, width, height, filled, queued, frontier);
            }

            var next = new List<int>();
            var pending = new List<int>();
            var colors = new List<Color32>();

            for (int pass = 0; pass < passes && frontier.Count > 0; pass++)
            {
                pending.Clear();
                colors.Clear();

                foreach (int i in frontier)
                {
                    queued[i] = false;
                    if (filled[i]) continue;

                    int x = i % width;
                    int y = i / width;
                    int r = 0;
                    int g = 0;
                    int b = 0;
                    int count = 0;

                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = x + ox;
                        int ny = y + oy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                        int n = ny * width + nx;
                        if (!filled[n]) continue;

                        r += pixels[n].r;
                        g += pixels[n].g;
                        b += pixels[n].b;
                        count++;
                    }

                    if (count == 0) continue;

                    pending.Add(i);
                    colors.Add(new Color32((byte) (r / count), (byte) (g / count), (byte) (b / count),
                        pixels[i].a));
                }

                if (pending.Count == 0) return;

                next.Clear();
                for (int k = 0; k < pending.Count; k++)
                {
                    pixels[pending[k]] = colors[k];
                    filled[pending[k]] = true;
                }

                // Only now that the whole ring is filled, so the next ring is measured from all of it.
                foreach (int i in pending) QueueNeighbours(i, width, height, filled, queued, next);

                frontier.Clear();
                frontier.AddRange(next);
            }
        }

        /// <summary>Adds the still-empty neighbours of a pixel to the next ring, once each.</summary>
        private static void QueueNeighbours(int index, int width, int height, bool[] filled, bool[] queued,
            List<int> destination)
        {
            int x = index % width;
            int y = index / width;

            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int nx = x + ox;
                int ny = y + oy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                int n = ny * width + nx;
                if (filled[n] || queued[n]) continue;

                queued[n] = true;
                destination.Add(n);
            }
        }

        /// <summary>The cleaned image as a drawable texture. The caller owns it and must destroy it.</summary>
        public static Texture2D CreateTexture(SpriteSnapshot snapshot, CleanupOptions options, CleanupPlan plan)
        {
            var texture = new Texture2D(plan.OutputWidth, plan.OutputHeight, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            texture.SetPixels32(BuildPixels(snapshot, options, plan));
            texture.Apply(false);
            return texture;
        }

        // -------------------------------------------------------------------------------------------
        // Import settings that have to follow the geometry
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Where the pivot has to move to so the sprite does not shift when its canvas does.
        /// <paramref name="pivot"/> and the result are normalised, as the importer stores them.
        /// </summary>
        public static Vector2 AdjustPivot(Vector2 pivot, int sourceWidth, int sourceHeight, CleanupPlan plan)
        {
            float x = pivot.x * sourceWidth - plan.Crop.xMin + plan.OffsetX;
            float y = pivot.y * sourceHeight - plan.Crop.yMin + plan.OffsetY;
            return new Vector2(x / Mathf.Max(1, plan.OutputWidth), y / Mathf.Max(1, plan.OutputHeight));
        }

        /// <summary>
        /// The 9-slice border, measured from edges that have just moved. A border that the crop ate
        /// into collapses to zero rather than going negative.
        /// </summary>
        public static NineSliceBorder AdjustBorder(NineSliceBorder border, int sourceWidth, int sourceHeight,
            CleanupPlan plan)
        {
            int trimmedRight = sourceWidth - plan.Crop.xMax;
            int trimmedTop = sourceHeight - plan.Crop.yMax;
            int paddedRight = plan.OutputWidth - plan.OffsetX - plan.Crop.width;
            int paddedTop = plan.OutputHeight - plan.OffsetY - plan.Crop.height;

            var adjusted = new NineSliceBorder(
                Mathf.Max(0, border.left - plan.Crop.xMin + plan.OffsetX),
                Mathf.Max(0, border.bottom - plan.Crop.yMin + plan.OffsetY),
                Mathf.Max(0, border.right - trimmedRight + paddedRight),
                Mathf.Max(0, border.top - trimmedTop + paddedTop));

            return adjusted.Clamped(plan.OutputWidth, plan.OutputHeight);
        }

        // -------------------------------------------------------------------------------------------
        // Writing
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Writes the cleaned image, either over the original - backed up first - or as a new file
        /// beside it, and carries the pivot and border across either way.
        ///
        /// <paramref name="message"/> always describes what happened, including skipped steps.
        /// </summary>
        public static bool Export(SpriteTarget target, SpriteSnapshot snapshot, CleanupOptions options,
            out string createdPath, out string message)
        {
            createdPath = null;
            options.Validate();

            if (options.DoesNothing)
            {
                message = "nothing to do - turn on bleed, trim or padding first.";
                return false;
            }

            if (snapshot == null)
            {
                message = "this image's pixels could not be read.";
                return false;
            }

            bool overwrite = options.Overwrites;
            string destination = overwrite
                ? target.DisplayPath
                : target.SiblingPath(SpriteImage.SanitizeSuffix(options.newFileSuffix, CleanupOptions.DefaultSuffix), ".png");
            string absolute = overwrite
                ? target.absolutePath
                : target.SiblingAbsolutePath(SpriteImage.SanitizeSuffix(options.newFileSuffix, CleanupOptions.DefaultSuffix), ".png");

            // Overwriting a .jpg with PNG bytes would leave a file whose contents disagree with its
            // name, so that one combination is refused rather than silently re-encoded.
            if (overwrite && Path.GetExtension(target.absolutePath).ToLowerInvariant() != ".png")
            {
                message = $"'{Path.GetExtension(target.absolutePath)}' cannot be overwritten with a " +
                          "cleaned image - the result needs PNG's alpha. Write a new file instead.";
                return false;
            }

            var plan = Plan(snapshot, options);
            var notes = new List<string>();

            var importer = target.IsExternal ? null : AssetImporter.GetAtPath(target.assetPath) as TextureImporter;

            // Sub-sprite rects are stored per sheet in coordinates this pass would move, and nothing
            // here can move them with it. Bleeding alone leaves the geometry alone, so it is allowed.
            if (plan.ChangesGeometry && importer != null && importer.spriteImportMode == SpriteImportMode.Multiple)
            {
                message = "Sprite Mode is Multiple, and trimming or padding would leave every sub-sprite " +
                          "rect pointing at the wrong pixels. Bleed on its own is safe here.";
                return false;
            }
            if (overwrite && options.createBackup && importer != null &&
                !SpriteBackups.Save(target.assetPath, importer, out string backupError))
            {
                // Refuse to overwrite art we could not back up first.
                message = backupError;
                return false;
            }

            try
            {
                var texture = CreateTexture(snapshot, options, plan);
                try
                {
                    byte[] bytes = texture.EncodeToPNG();
                    if (bytes == null || bytes.Length == 0)
                    {
                        message = "encoding produced no data.";
                        return false;
                    }

                    File.WriteAllBytes(absolute, bytes);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            catch (Exception exception)
            {
                message = $"no file written ({exception.Message})";
                return false;
            }

            if (options.bleed) notes.Add($"bled {options.bleedPasses}px");
            notes.Add(plan.ChangesGeometry
                ? $"{snapshot.Width}x{snapshot.Height} -> {plan.OutputWidth}x{plan.OutputHeight}"
                : "size unchanged");

            createdPath = destination;
            if (target.IsExternal)
            {
                notes.Add("written outside the project, so nothing was imported");
                message = string.Join(", ", notes);
                return true;
            }

            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
            WriteImportSettings(destination, target.assetPath, snapshot, plan, notes);

            message = string.Join(", ", notes);
            return true;
        }

        /// <summary>
        /// Moves the pivot and border of whichever file was just written so the sprite still lines
        /// up with where it used to be.
        /// </summary>
        private static void WriteImportSettings(string createdPath, string sourcePath, SpriteSnapshot snapshot,
            CleanupPlan plan, List<string> notes)
        {
            if (!(AssetImporter.GetAtPath(createdPath) is TextureImporter created)) return;

            var source = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (source != null && createdPath != sourcePath)
            {
                var settings = new TextureImporterSettings();
                source.ReadTextureSettings(settings);
                created.SetTextureSettings(settings);
                created.maxTextureSize = source.maxTextureSize;
                created.spriteImportMode = SpriteImportMode.Single;
            }

            if (!plan.ChangesGeometry)
            {
                created.SaveAndReimport();
                return;
            }

            var reference = source ?? created;
            var border = NineSliceBorder.FromVector4(reference.spriteBorder);
            var pivot = reference.spritePivot;
            int alignment = SpriteBackups.ReadAlignment(reference);

            var movedPivot = AdjustPivot(pivot, snapshot.Width, snapshot.Height, plan);
            SpriteBackups.WritePivot(created, movedPivot, (int) SpriteAlignment.Custom);

            if (!border.IsZero)
            {
                var movedBorder = AdjustBorder(border, snapshot.Width, snapshot.Height, plan);
                created.spriteBorder = movedBorder.ToVector4();
                notes.Add($"border {border} -> {movedBorder}");
            }

            created.SaveAndReimport();

            // Worth saying out loud: a pivot that was a named preset is now a number, which is the
            // only way to express "a bit off centre" once the canvas has moved under it.
            if (alignment != (int) SpriteAlignment.Custom)
                notes.Add($"pivot {pivot} -> {movedPivot} (alignment now Custom)");
        }
    }
}
