using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// A CPU-readable copy of a texture's pixels together with the texture object used to draw it.
    /// The snapshot owns that Texture2D, so it must be disposed - otherwise the editor leaks one
    /// full-size texture for every asset that gets inspected.
    /// </summary>
    public sealed class SpriteSnapshot : IDisposable
    {
        /// <summary>Point-filtered copy safe to draw and to mutate filterMode on (we own it).</summary>
        public Texture2D Texture { get; private set; }

        /// <summary>Row-major, first pixel at the bottom-left - Unity's texture convention.</summary>
        public Color32[] Pixels { get; private set; }

        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// True when the pixels were decoded from the file on disk, which is the same coordinate
        /// space as <see cref="TextureImporter.spriteBorder"/>. False when they were read back from
        /// the imported texture, which import settings may have downscaled - anything measured on
        /// those pixels is only correct if the import did not resize the texture.
        /// </summary>
        public bool IsSourceResolution { get; }

        internal SpriteSnapshot(Texture2D texture, bool isSourceResolution)
        {
            Texture = texture;
            Width = texture.width;
            Height = texture.height;
            Pixels = texture.GetPixels32();
            IsSourceResolution = isSourceResolution;

            Texture.filterMode = FilterMode.Point;
            Texture.wrapMode = TextureWrapMode.Clamp;
        }

        public void Dispose()
        {
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
            Pixels = null;
        }
    }

    /// <summary>
    /// Reading pixels off an asset and turning paths into files. Shared by every tool in the sprite
    /// editor, none of which can rely on "Read/Write Enabled" being ticked on the art it is handed.
    /// </summary>
    public static class SpriteImage
    {
        /// <summary>Prefix for every console message the sprite editor writes.</summary>
        public const string Log = "[Sprite Editor]";

        /// <summary>Extensions whose bytes we can both decode and re-encode.</summary>
        public static bool IsRewritableImage(string assetPath)
        {
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        }

        /// <summary>
        /// Reads a texture's pixels without caring about its "Read/Write Enabled" flag or its
        /// compressed runtime format. Returns null and fills <paramref name="error"/> on failure.
        /// </summary>
        public static SpriteSnapshot Load(string assetPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(assetPath))
            {
                error = "No asset path.";
                return null;
            }

            // Preferred path: decode the file itself. Gives the true source pixels at source
            // resolution, uncompressed, whatever the import settings happen to be.
            if (IsRewritableImage(assetPath))
            {
                var decoded = LoadFile(ToAbsolutePath(assetPath), out error);
                if (decoded != null) return decoded;

                // A file that will not decode is not fatal here: the imported texture below is a
                // second chance at the same pixels.
                error = null;
            }

            // Fallback for .psd/.tga/.exr/... : pull the imported texture back off the GPU. Works
            // for any format and ignores isReadable, but yields the *imported* pixels.
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (asset == null)
            {
                error = $"'{assetPath}' is not a Texture2D.";
                return null;
            }

            var readback = ReadBack(asset);
            if (readback == null)
            {
                error = $"Could not read the pixels of '{Path.GetFileName(assetPath)}'.";
                return null;
            }

            return new SpriteSnapshot(readback, false);
        }

        /// <summary>
        /// Decodes an image file straight off disk, with no AssetDatabase involved - the only way in
        /// for a file that lives outside the project. Returns null and fills
        /// <paramref name="error"/> on failure.
        /// </summary>
        public static SpriteSnapshot LoadFile(string absolutePath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(absolutePath))
            {
                error = "No file path.";
                return null;
            }

            if (!IsRewritableImage(absolutePath))
            {
                error = $"'{Path.GetExtension(absolutePath)}' cannot be decoded directly. Files from " +
                        "outside the project have to be .png or .jpg.";
                return null;
            }

            if (!File.Exists(absolutePath))
            {
                error = $"'{absolutePath}' does not exist.";
                return null;
            }

            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (decoded.LoadImage(File.ReadAllBytes(absolutePath), false))
                    return new SpriteSnapshot(decoded, true);

                error = $"'{Path.GetFileName(absolutePath)}' could not be decoded.";
            }
            catch (Exception exception)
            {
                error = $"Could not read '{absolutePath}': {exception.Message}";
            }

            UnityEngine.Object.DestroyImmediate(decoded);
            return null;
        }

        private static Texture2D ReadBack(Texture2D source)
        {
            var renderTexture = RenderTexture.GetTemporary(
                source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                copy.Apply(false);
                return copy;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{Log} GPU read-back failed for '{source.name}': {exception.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        /// <summary>
        /// Absolute path of an asset. Derived from dataPath rather than the working directory,
        /// which Unity does not guarantee stays at the project root.
        /// </summary>
        /// <summary>
        /// The project-relative path for an absolute one under Assets/, or null when it is somewhere
        /// else on disk. The inverse of <see cref="ToAbsolutePath"/>.
        /// </summary>
        public static string ToAssetPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;

            string normalized = absolutePath.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');

            return normalized.StartsWith(dataPath + "/", System.StringComparison.OrdinalIgnoreCase)
                ? "Assets/" + normalized.Substring(dataPath.Length + 1)
                : null;
        }

        public static string ToAbsolutePath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    // resolvedPath already points at the package root, so drop "Packages/<name>".
                    string remainder = assetPath.Substring(Mathf.Min(assetPath.Length,
                        "Packages/".Length + package.name.Length)).TrimStart('/');
                    return Path.Combine(package.resolvedPath, remainder);
                }
            }

            return Path.Combine(ProjectRoot, assetPath);
        }

        public static string ProjectRoot =>
            Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();

        /// <summary>
        /// Strips anything that would move a derived file out of its folder or make an illegal
        /// name, and never returns empty.
        ///
        /// An empty suffix is the dangerous case: it would make the derived path identical to the
        /// source and quietly overwrite the art it was made from.
        /// </summary>
        public static string SanitizeSuffix(string suffix, string fallback)
        {
            if (string.IsNullOrEmpty(suffix)) return fallback;

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(suffix.Length);
            foreach (char character in suffix)
            {
                if (character == '/' || character == '\\') continue;
                if (Array.IndexOf(invalid, character) >= 0) continue;
                builder.Append(character);
            }

            string cleaned = builder.ToString().Trim();
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        /// <summary>
        /// Folder part of an asset path, forward-slashed. Empty only for paths with no folder at
        /// all, which no real asset has.
        /// </summary>
        public static string GetAssetDirectory(string assetPath)
        {
            string directory = Path.GetDirectoryName(assetPath);
            return string.IsNullOrEmpty(directory) ? string.Empty : directory.Replace('\\', '/');
        }
    }
}
