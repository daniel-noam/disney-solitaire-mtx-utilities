using System;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>How the canvas is rounded up, for hardware that would rather have a power of two.</summary>
    public enum CleanupPadding
    {
        /// <summary>Leave the size alone.</summary>
        None = 0,

        /// <summary>Round each axis up to the next power of two, independently.</summary>
        PowerOfTwo = 1,

        /// <summary>Round up to a square power of two.</summary>
        SquarePowerOfTwo = 2,

        /// <summary>Round each axis up to the next multiple of 4, independently.</summary>
        MultipleOfFour = 3,
    }

    /// <summary>Where the art sits inside a canvas bigger than itself.</summary>
    public enum CleanupAnchor
    {
        Center = 0,
        BottomLeft = 1,
    }

    /// <summary>
    /// Settings for the cleanup pass. Persisted per user via EditorPrefs - these are working
    /// preferences rather than project data, so they deliberately do not live in ProjectSettings/
    /// or in an asset.
    /// </summary>
    [Serializable]
    public class CleanupOptions
    {
        [Tooltip("Push the colour of the visible art outwards into the transparent pixels around " +
                 "it. Fixes the dark fringe that bilinear filtering pulls in from the black hiding " +
                 "behind alpha 0.")]
        public bool bleed = true;

        [Tooltip("How far the colour is pushed. Each pass covers one more pixel.")]
        public int bleedPasses = 4;

        [Tooltip("Crop the fully transparent margin away.")]
        public bool trim = false;

        [Tooltip("Alpha at or above this counts as content worth keeping.")]
        public int trimAlpha = 1;

        [Tooltip("Transparent pixels left around the content after trimming.")]
        public int trimMargin = 0;

        public CleanupPadding padding = CleanupPadding.None;

        [Tooltip("Where the art sits inside a canvas bigger than itself.")]
        public CleanupAnchor anchor = CleanupAnchor.Center;

        [Tooltip("Act on the original asset. Off writes a new file beside it instead and leaves the " +
                 "original completely alone, import settings included.")]
        public bool overwriteOriginal = false;

        [Tooltip("Added to the file name in new-file mode. The new file is written to the same " +
                 "folder as the original.")]
        public string newFileSuffix = DefaultSuffix;

        public bool createBackup = true;

        private const string PrefsKey = "Utilities.Editor.SpriteEditor.CleanupOptions";
        public const string DefaultSuffix = "-clean";

        /// <summary>Nothing to do, so the buttons and the preview can say so.</summary>
        public bool DoesNothing => !bleed && !trim && padding == CleanupPadding.None;

        /// <summary>The one destructive combination: the original's pixels get replaced.</summary>
        public bool Overwrites => overwriteOriginal;

        public void Validate()
        {
            bleedPasses = Mathf.Clamp(bleedPasses, 1, 64);
            trimAlpha = Mathf.Clamp(trimAlpha, 1, 255);
            trimMargin = Mathf.Clamp(trimMargin, 0, 256);
            newFileSuffix = SpriteImage.SanitizeSuffix(newFileSuffix, DefaultSuffix);
        }

        public void Save()
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
        }

        public static CleanupOptions Load()
        {
            var options = new CleanupOptions();
            string json = EditorPrefs.GetString(PrefsKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(json, options);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"{SpriteImage.Log} Could not read saved cleanup options, using " +
                                     $"defaults: {exception.Message}");
                    options = new CleanupOptions();
                }
            }

            options.Validate();
            return options;
        }
    }

    /// <summary>
    /// What a cleanup pass would do to the image's geometry: which part of the source survives, how
    /// big the result is, and where the surviving part lands in it. Everything else - the pivot, the
    /// 9-slice border, the info line - is derived from these numbers.
    /// </summary>
    public struct CleanupPlan
    {
        /// <summary>Region of the source that is kept, in source pixels, y up from the bottom.</summary>
        public RectInt Crop;

        public int OutputWidth;
        public int OutputHeight;

        /// <summary>Where <see cref="Crop"/>'s bottom-left corner lands in the output.</summary>
        public int OffsetX;
        public int OffsetY;

        public bool ChangesGeometry;

        public int TrimmedLeft => Crop.xMin;
        public int TrimmedBottom => Crop.yMin;
        public int PaddedLeft => OffsetX;
        public int PaddedBottom => OffsetY;
    }
}
