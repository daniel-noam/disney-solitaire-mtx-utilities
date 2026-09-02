using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// The image the window is pointed at, which is either a project asset or a file anywhere on
    /// disk. Everything a tool needs to know about *where* it lives goes through here, so the tools
    /// themselves never have to branch on it - they ask for the sibling path and get one in the
    /// right space.
    ///
    /// Serializable so a half-finished edit survives a script recompile - losing a hand-tuned border
    /// or a picked mask colour to a domain reload is infuriating.
    /// </summary>
    [Serializable]
    public class SpriteTarget
    {
        /// <summary>The imported asset. Null for a file outside the project.</summary>
        public Texture2D asset;

        /// <summary>Asset path. Empty for a file outside the project.</summary>
        public string assetPath = string.Empty;

        /// <summary>Where the file actually is on disk. Always set.</summary>
        public string absolutePath = string.Empty;

        /// <summary>
        /// Already imported as a Sprite. Anything else - a Spine/atlas page, a tiled material
        /// texture - is worth warning about before a tool starts writing import settings.
        /// </summary>
        public bool isSprite;

        public string textureTypeName = string.Empty;

        /// <summary>
        /// Outside the project, so there are no import settings: no border to write, no pivot to
        /// copy, nothing to ping in the Project window. Pixels still work.
        /// </summary>
        public bool IsExternal => string.IsNullOrEmpty(assetPath);

        public string FileName =>
            string.IsNullOrEmpty(absolutePath) ? string.Empty : Path.GetFileName(absolutePath);

        /// <summary>What to show and log: the asset path in the project, the full path outside it.</summary>
        public string DisplayPath => IsExternal ? absolutePath : assetPath;

        public static SpriteTarget FromAsset(Texture2D asset)
        {
            if (asset == null) return null;

            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;

            var target = FromAssetPath(path);
            target.asset = asset;
            return target;
        }

        public static SpriteTarget FromAssetPath(string assetPath)
        {
            return new SpriteTarget
            {
                assetPath = assetPath,
                absolutePath = SpriteImage.ToAbsolutePath(assetPath),
                asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath),
            };
        }

        /// <summary>
        /// A file picked off disk. One that turns out to live under Assets/ comes back as a normal
        /// project target instead - dragging your own art in from Finder should not quietly cut it
        /// off from its import settings.
        /// </summary>
        public static SpriteTarget FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string normalized = path.Replace('\\', '/');

            // Drags out of the Project window hand over asset paths, which are relative.
            if (!Path.IsPathRooted(normalized)) return FromAssetPath(normalized);

            string dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                string assetPath = "Assets/" + normalized.Substring(dataPath.Length + 1);
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath) != null) return FromAssetPath(assetPath);
            }

            return new SpriteTarget { absolutePath = normalized };
        }

        /// <summary>
        /// Path of a derived file beside this one - same folder, suffix on the name - in whatever
        /// space this target lives in, so it can be shown and compared against
        /// <see cref="DisplayPath"/>.
        /// </summary>
        public string SiblingPath(string suffix, string extension)
        {
            // A texture that is not a saved asset has neither path: GetAssetPath returns empty, which
            // makes IsExternal true while absolutePath is still empty. Path.GetDirectoryName("")
            // throws rather than returning nothing, and this is called while drawing — so the throw
            // escaped mid-layout and took the clip stack with it.
            if (string.IsNullOrEmpty(absolutePath)) return string.Empty;

            string name = Path.GetFileNameWithoutExtension(absolutePath) + suffix + extension;

            if (IsExternal)
            {
                string directory = Path.GetDirectoryName(absolutePath);
                return string.IsNullOrEmpty(directory)
                    ? name
                    : (directory + "/" + name).Replace('\\', '/');
            }

            string folder = SpriteImage.GetAssetDirectory(assetPath);
            return string.IsNullOrEmpty(folder) ? name : folder + "/" + name;
        }

        /// <summary>The same file, as somewhere File.WriteAllBytes can actually put it.</summary>
        public string SiblingAbsolutePath(string suffix, string extension)
        {
            string path = SiblingPath(suffix, extension);
            return IsExternal ? path : SpriteImage.ToAbsolutePath(path);
        }

        /// <summary>Folder the sibling would land in, checked before anything is written.</summary>
        public bool SiblingFolderExists(string suffix, string extension)
        {
            if (IsExternal)
            {
                string directory = Path.GetDirectoryName(SiblingAbsolutePath(suffix, extension));
                return !string.IsNullOrEmpty(directory) && Directory.Exists(directory);
            }

            return AssetDatabase.IsValidFolder(SpriteImage.GetAssetDirectory(SiblingPath(suffix, extension)));
        }
    }
}
