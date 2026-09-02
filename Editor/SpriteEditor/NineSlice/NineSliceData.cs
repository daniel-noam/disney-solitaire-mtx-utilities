using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>
    /// How two neighbouring pixel lines are judged to be "the same" while looking for the
    /// stretchable band of a texture.
    /// </summary>
    public enum NineSliceComparison
    {
        /// <summary>Every pixel must match within the tolerance (aside from the allowed outliers).</summary>
        EveryPixel = 0,

        /// <summary>The mean difference across the line must stay within the tolerance. More
        /// forgiving of dithering and JPEG ringing, but a single strong feature can slip through.</summary>
        AverageDifference = 1,
    }


    /// <summary>
    /// Border thickness in pixels. Field order matches <see cref="TextureImporter.spriteBorder"/>,
    /// which packs the border as (left, bottom, right, top).
    /// </summary>
    [Serializable]
    public struct NineSliceBorder : IEquatable<NineSliceBorder>
    {
        public int left;
        public int bottom;
        public int right;
        public int top;

        public NineSliceBorder(int left, int bottom, int right, int top)
        {
            this.left = left;
            this.bottom = bottom;
            this.right = right;
            this.top = top;
        }

        public static NineSliceBorder Zero => new NineSliceBorder(0, 0, 0, 0);

        public bool IsZero => left == 0 && bottom == 0 && right == 0 && top == 0;

        public Vector4 ToVector4()
        {
            return new Vector4(left, bottom, right, top);
        }

        public static NineSliceBorder FromVector4(Vector4 value)
        {
            return new NineSliceBorder(
                Mathf.RoundToInt(value.x),
                Mathf.RoundToInt(value.y),
                Mathf.RoundToInt(value.z),
                Mathf.RoundToInt(value.w));
        }

        /// <summary>
        /// Keeps the border inside the texture, leaving at least one pixel of stretchable centre on
        /// each axis. A 1px centre is legal for 9-slicing, so only a fully collapsed centre is rejected.
        /// </summary>
        public NineSliceBorder Clamped(int width, int height)
        {
            int clampedLeft = Mathf.Clamp(left, 0, Mathf.Max(0, width - 1));
            int clampedRight = Mathf.Clamp(right, 0, Mathf.Max(0, width - 1 - clampedLeft));
            int clampedBottom = Mathf.Clamp(bottom, 0, Mathf.Max(0, height - 1));
            int clampedTop = Mathf.Clamp(top, 0, Mathf.Max(0, height - 1 - clampedBottom));
            return new NineSliceBorder(clampedLeft, clampedBottom, clampedRight, clampedTop);
        }

        /// <summary>
        /// Equalises opposing borders by taking the larger of the two.
        ///
        /// Deliberately not the smaller one: the detected border is the part of the image that is
        /// *not* uniform, so shrinking a border pulls non-uniform pixels into the stretched centre
        /// and they smear at runtime. Growing it only moves uniform pixels into the border, which
        /// costs a little texture space and looks identical.
        /// </summary>
        public NineSliceBorder Symmetrical()
        {
            int horizontal = Mathf.Max(left, right);
            int vertical = Mathf.Max(bottom, top);
            return new NineSliceBorder(horizontal, vertical, horizontal, vertical);
        }

        public bool Equals(NineSliceBorder other)
        {
            return left == other.left && bottom == other.bottom && right == other.right && top == other.top;
        }

        public override bool Equals(object obj)
        {
            return obj is NineSliceBorder other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (left * 397 ^ bottom) * 397 ^ (right * 397 ^ top);
        }

        public override string ToString()
        {
            return $"{left}, {bottom}, {right}, {top}";
        }
    }

    /// <summary>
    /// Detection and output settings for the 9-slice tools. Persisted per user via EditorPrefs -
    /// these are working preferences rather than project data, so they deliberately do not live in
    /// ProjectSettings/ or in an asset.
    /// </summary>
    [Serializable]
    public class NineSliceOptions
    {
        // --- Detection -------------------------------------------------------------------------

        [Tooltip("Largest per-channel difference (0-255) still counted as 'no change' between two " +
                 "neighbouring rows or columns. 0 requires an exact match.")]
        public int tolerance = 0;

        public NineSliceComparison comparison = NineSliceComparison.EveryPixel;

        [Tooltip("How many pixels in a line may exceed the tolerance and still count as unchanged. " +
                 "Lets a stray antialiased pixel through without opening up the tolerance for everything.")]
        public int allowedOutliers = 0;

        [Tooltip("Pixels trimmed off each end of the detected stretchable band, i.e. added to the " +
                 "border. Guards against bilinear filtering sampling across a slice boundary.")]
        public int margin = 2;

        public bool detectHorizontal = true;

        public bool detectVertical = true;

        [Tooltip("Force left = right and bottom = top by growing the thinner side.")]
        public bool symmetricBorders = false;

        [Tooltip("Treat all fully transparent pixels as equal regardless of their RGB. Painting " +
                 "tools leave arbitrary colour behind zero alpha, which otherwise breaks detection.")]
        public bool ignoreTransparentColor = true;

        // --- Output ----------------------------------------------------------------------------

        [Tooltip("Only set the importer's border and leave the image file alone. Same result as " +
                 "dragging the handles in Unity's Sprite Editor, but with auto-detection.")]
        public bool borderOnly = false;

        [Tooltip("Act on the original asset. Off writes a new file beside it instead and leaves the " +
                 "original completely alone, import settings included.")]
        public bool overwriteOriginal = false;

        /// <summary>Whether the stretchable centre is cut out. Independent of which asset is written.</summary>
        public bool CutsPixels => !borderOnly;

        /// <summary>Which asset the whole operation acts on.</summary>
        public bool TargetsOriginal => overwriteOriginal;

        public bool TargetsNewFile => !overwriteOriginal;

        /// <summary>The one destructive combination: the original's pixels get replaced.</summary>
        public bool Overwrites => !borderOnly && overwriteOriginal;

        [Tooltip("Added to the file name in Create New File mode. The new file is written to the " +
                 "same folder as the original.")]
        public string newFileSuffix = DefaultSuffix;

        [Tooltip("Width/height the stretchable centre is collapsed to when compressing.")]
        public int centerSize = 2;

        public bool createBackup = true;

        [Tooltip("Re-encode quality for .jpg/.jpeg targets. JPEG is lossy, so compressing a JPEG " +
                 "always costs some quality.")]
        public int jpgQuality = 95;

        private const string PrefsKey = "Utilities.Editor.NineSlice.Options";
        public const string DefaultSuffix = "-9slice";

        public void Validate()
        {
            tolerance = Mathf.Clamp(tolerance, 0, 255);
            allowedOutliers = Mathf.Max(0, allowedOutliers);
            margin = Mathf.Max(0, margin);
            centerSize = Mathf.Clamp(centerSize, 1, 256);
            jpgQuality = Mathf.Clamp(jpgQuality, 1, 100);
            newFileSuffix = SanitizeSuffix(newFileSuffix);
        }

        /// <summary>Keeps the new file's name legal and never equal to the original's.</summary>
        public static string SanitizeSuffix(string suffix)
        {
            return SpriteImage.SanitizeSuffix(suffix, DefaultSuffix);
        }

        /// <summary>
        /// Path of the compressed sibling: same folder, same extension, suffix on the name. Asset
        /// paths are always forward-slashed, whatever the platform.
        /// </summary>
        public static string BuildNewAssetPath(string assetPath, string suffix)
        {
            suffix = SanitizeSuffix(suffix);
            string directory = Path.GetDirectoryName(assetPath);
            string name = Path.GetFileNameWithoutExtension(assetPath) + suffix + Path.GetExtension(assetPath);
            string combined = string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
            return combined.Replace('\\', '/');
        }

        public void Save()
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
        }

        public static NineSliceOptions Load()
        {
            var options = new NineSliceOptions();
            string json = EditorPrefs.GetString(PrefsKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(json, options);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"{SpriteImage.Log} Could not read saved options, using defaults: {exception.Message}");
                    options = new NineSliceOptions();
                }
            }

            options.Validate();
            return options;
        }
    }

    /// <summary>Outcome of a detection pass, including why an axis was left unsliced.</summary>
    public struct NineSliceDetectionResult
    {
        public NineSliceBorder Border;
        public bool FoundHorizontal;
        public bool FoundVertical;

        /// <summary>Human readable summary for the window's status line. Never null.</summary>
        public string Message;
    }
}
