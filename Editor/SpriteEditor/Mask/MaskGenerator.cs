using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Turns a sprite into a flat-coloured copy of its own silhouette and writes it out as a new
    /// texture - the quickest way to get the mask, glow layer or shadow plate that goes under a
    /// piece of art without opening Photoshop.
    ///
    /// Nothing here ever writes to the source: a mask is always a new file.
    /// </summary>
    public static class MaskGenerator
    {
        /// <summary>
        /// Mask pixels for a snapshot, same size and same row order as the source.
        ///
        /// Every pixel takes the chosen colour; only its alpha carries the shape, which is what
        /// makes the result usable both as a UI mask and as a solid silhouette behind the original.
        /// </summary>
        public static Color32[] BuildPixels(SpriteSnapshot snapshot, MaskOptions options)
        {
            return BuildPixels(snapshot, options, options.color);
        }

        /// <summary>
        /// Same, with the colour supplied separately. The window builds its preview white and tints
        /// it while drawing, so picking a colour costs nothing - only the settings that change the
        /// *shape* need the pixels walked again.
        /// </summary>
        public static Color32[] BuildPixels(SpriteSnapshot snapshot, MaskOptions options, Color32 tint)
        {
            if (snapshot == null) return Array.Empty<Color32>();
            options.Validate();

            var coverage = BuildCoverage(snapshot, options);
            if (options.grow > 0) coverage = Grow(coverage, snapshot.Width, snapshot.Height, options);

            var output = new Color32[coverage.Length];
            int tintAlpha = tint.a;
            for (int i = 0; i < coverage.Length; i++)
                output[i] = new Color32(tint.r, tint.g, tint.b, (byte) (coverage[i] * tintAlpha / 255));

            return output;
        }

        /// <summary>
        /// How solid the mask is at every pixel, before any growing: the shape itself, 0-255.
        /// </summary>
        private static byte[] BuildCoverage(SpriteSnapshot snapshot, MaskOptions options)
        {
            var source = snapshot.Pixels;
            var coverage = new byte[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                var pixel = source[i];

                int value;
                switch (options.shape)
                {
                    case MaskShape.Luminance:
                        // Weighted for how bright each channel actually looks, then scaled by alpha
                        // so a transparent white pixel does not read as solid.
                        int luminance = (pixel.r * 54 + pixel.g * 183 + pixel.b * 19) >> 8;
                        value = luminance * pixel.a / 255;
                        break;
                    case MaskShape.Everything:
                        value = 255;
                        break;
                    default:
                        value = pixel.a;
                        break;
                }

                if (options.invert) value = 255 - value;
                if (options.edges == MaskEdges.Threshold) value = value >= options.threshold ? 255 : 0;

                coverage[i] = (byte) value;
            }

            return coverage;
        }

        /// <summary>
        /// Expands the shape by <c>options.grow</c> pixels, or keeps only the ring that expansion
        /// added when asked for an outline.
        ///
        /// The distance to the nearest solid pixel comes from a two-pass chamfer transform with 3/4
        /// weights - within a few percent of a true euclidean distance, which is what stops the
        /// corners of a grown shape coming out square.
        /// </summary>
        private static byte[] Grow(byte[] coverage, int width, int height, MaskOptions options)
        {
            // Everything at or above this seeds the growth. Soft edges below it still get their
            // distance measured from the solid core, so a feathered shape does not grow twice.
            int seed = options.edges == MaskEdges.Threshold ? options.threshold : 128;

            const int Orthogonal = 3;
            const int Diagonal = 4;
            int far = (options.grow + 2) * Orthogonal + Diagonal;

            var distance = new int[coverage.Length];
            for (int i = 0; i < coverage.Length; i++) distance[i] = coverage[i] >= seed ? 0 : far;

            // Forward: every pixel takes the best of its already-visited neighbours...
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int best = distance[i];
                if (best == 0) continue;

                if (x > 0) best = Mathf.Min(best, distance[i - 1] + Orthogonal);
                if (y > 0)
                {
                    best = Mathf.Min(best, distance[i - width] + Orthogonal);
                    if (x > 0) best = Mathf.Min(best, distance[i - width - 1] + Diagonal);
                    if (x < width - 1) best = Mathf.Min(best, distance[i - width + 1] + Diagonal);
                }

                distance[i] = best;
            }

            // ...and backward, which is what makes the result symmetric.
            for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x;
                int best = distance[i];
                if (best == 0) continue;

                if (x < width - 1) best = Mathf.Min(best, distance[i + 1] + Orthogonal);
                if (y < height - 1)
                {
                    best = Mathf.Min(best, distance[i + width] + Orthogonal);
                    if (x > 0) best = Mathf.Min(best, distance[i + width - 1] + Diagonal);
                    if (x < width - 1) best = Mathf.Min(best, distance[i + width + 1] + Diagonal);
                }

                distance[i] = best;
            }

            var grown = new byte[coverage.Length];
            for (int i = 0; i < coverage.Length; i++)
            {
                // Solid out to the requested radius, then one pixel of falloff, so the new edge is
                // as smooth as the one it replaces.
                float pixels = distance[i] / (float) Orthogonal;
                int ring = Mathf.RoundToInt(Mathf.Clamp01(options.grow + 1f - pixels) * 255f);

                grown[i] = options.outlineOnly
                    ? (byte) Mathf.Max(0, ring - coverage[i])
                    : (byte) Mathf.Max(ring, coverage[i]);
            }

            return grown;
        }

        /// <summary>Mask as a drawable texture. The caller owns it and must destroy it.</summary>
        public static Texture2D CreateTexture(SpriteSnapshot snapshot, MaskOptions options)
        {
            return CreateTexture(snapshot, options, options.color);
        }

        /// <summary>Same, with the colour supplied separately.</summary>
        public static Texture2D CreateTexture(SpriteSnapshot snapshot, MaskOptions options, Color32 tint)
        {
            if (snapshot == null) return null;

            var texture = new Texture2D(snapshot.Width, snapshot.Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            texture.SetPixels32(BuildPixels(snapshot, options, tint));
            texture.Apply(false);
            return texture;
        }

        /// <summary>
        /// Writes the mask for <paramref name="sourceAssetPath"/> and imports it.
        ///
        /// <paramref name="snapshot"/> may be null, in which case the pixels are read here - that is
        /// what the batch menu does, having no window to borrow a snapshot from.
        ///
        /// <paramref name="overwrite"/> has to be granted by the caller: silently replacing a mask
        /// that someone has since hand-edited is exactly the kind of surprise this tool should not
        /// spring.
        /// </summary>
        public static bool Export(SpriteTarget target, SpriteSnapshot snapshot, MaskOptions options,
            bool overwrite, out string createdPath, out string message) =>
            Export(target, snapshot, options, overwrite, null, out createdPath, out message);

        /// <param name="destinationOverride">
        /// Where to write, when the caller has asked the user instead of taking the suffix's answer.
        /// A project-relative path for somewhere under Assets/, an absolute one otherwise. Null keeps
        /// the derived sibling path.
        /// </param>
        public static bool Export(SpriteTarget target, SpriteSnapshot snapshot, MaskOptions options,
            bool overwrite, string destinationOverride, out string createdPath, out string message)
        {
            createdPath = null;
            options.Validate();

            bool chosen = !string.IsNullOrEmpty(destinationOverride);

            string destination = chosen ? destinationOverride : options.BuildOutputPath(target);
            string absolute = chosen
                ? (Path.IsPathRooted(destinationOverride)
                    ? destinationOverride
                    : SpriteImage.ToAbsolutePath(destinationOverride))
                : options.BuildOutputAbsolutePath(target);

            // A folder the user picked in a save panel exists by construction; only the derived
            // sibling path can point somewhere that does not.
            if (!chosen && !target.SiblingFolderExists(MaskOptions.SanitizeSuffix(options.suffix), ".png"))
            {
                message = $"'{SpriteImage.GetAssetDirectory(destination)}' is not a folder that can be " +
                          "written to.";
                return false;
            }

            if (destination == target.DisplayPath)
            {
                message = "the mask would overwrite the source image - change the suffix.";
                return false;
            }

            bool existed = File.Exists(absolute);
            if (existed && !overwrite)
            {
                message = $"{Path.GetFileName(destination)} already exists.";
                return false;
            }

            // Borrowed snapshots stay alive for the caller; one loaded here is ours to release.
            SpriteSnapshot owned = null;
            var pixels = snapshot;
            if (pixels == null)
            {
                owned = target.IsExternal
                    ? SpriteImage.LoadFile(target.absolutePath, out string loadError)
                    : SpriteImage.Load(target.assetPath, out loadError);

                pixels = owned;
                if (pixels == null)
                {
                    message = string.IsNullOrEmpty(loadError) ? $"could not read '{target.FileName}'." : loadError;
                    return false;
                }
            }

            try
            {
                var texture = CreateTexture(pixels, options);
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

                // A mask written outside the project is just a file: nothing to import, and no
                // source importer to copy settings from.
                if (!target.IsExternal)
                {
                    AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
                    ApplyImportSettings(destination, target.assetPath, options.copyImportSettings);
                }

                createdPath = destination;
                message = $"{(existed ? "replaced" : "wrote")} {Path.GetFileName(destination)} " +
                          $"({pixels.Width}x{pixels.Height})";
                return true;
            }
            catch (Exception exception)
            {
                message = $"no file written ({exception.Message})";
                return false;
            }
            finally
            {
                owned?.Dispose();
            }
        }

        /// <summary>
        /// Makes the new file behave like the sprite it was made from. Without this a mask lands on
        /// the project's default texture settings and no longer lines up with its source - different
        /// pivot, different pixels per unit, no 9-slice border.
        /// </summary>
        private static void ApplyImportSettings(string createdPath, string sourcePath, bool copyFromSource)
        {
            if (!(AssetImporter.GetAtPath(createdPath) is TextureImporter created)) return;

            if (copyFromSource && AssetImporter.GetAtPath(sourcePath) is TextureImporter source)
            {
                var settings = new TextureImporterSettings();
                source.ReadTextureSettings(settings);
                created.SetTextureSettings(settings);
                created.maxTextureSize = source.maxTextureSize;
            }

            created.textureType = TextureImporterType.Sprite;

            // A sprite sheet's sub-sprite rects are not part of TextureImporterSettings, so copying
            // Multiple across would leave the mask with no sprites at all.
            created.spriteImportMode = SpriteImportMode.Single;
            created.alphaIsTransparency = true;
            created.SaveAndReimport();
        }
    }
}
