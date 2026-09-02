using System;
using UnityEditor;
using UnityEngine;

namespace Utilities.Editor
{
    /// <summary>Which part of the source decides where the mask is solid.</summary>
    public enum MaskShape
    {
        /// <summary>The sprite's own alpha - the silhouette. What a mask usually wants.</summary>
        Alpha = 0,

        /// <summary>How bright the pixel is, so grey-scale art becomes a mask of itself.</summary>
        Luminance = 1,

        /// <summary>Every pixel, transparent ones included: a solid rectangle the size of the sprite.</summary>
        Everything = 2,
    }

    /// <summary>What happens to the coverage value once it has been read.</summary>
    public enum MaskEdges
    {
        /// <summary>Keep it as-is, so antialiased edges stay soft.</summary>
        Keep = 0,

        /// <summary>Snap it to fully on or fully off - a hard-edged, 1-bit mask.</summary>
        Threshold = 1,
    }

    /// <summary>
    /// Settings for the mask creator. Persisted per user via EditorPrefs - these are working
    /// preferences rather than project data, so they deliberately do not live in ProjectSettings/
    /// or in an asset.
    /// </summary>
    [Serializable]
    public class MaskOptions
    {
        [Tooltip("Colour every pixel of the mask is painted with. Its alpha scales the whole mask.")]
        public Color color = Color.white;

        [Tooltip("Which part of the source decides where the mask is solid.")]
        public MaskShape shape = MaskShape.Alpha;

        [Tooltip("Keep soft edges, or snap every pixel to fully on or fully off.")]
        public MaskEdges edges = MaskEdges.Keep;

        [Tooltip("Coverage at or above this counts as solid. Only used with hard edges.")]
        public int threshold = 128;

        [Tooltip("Swap solid and empty, for a mask of everything the sprite does not cover.")]
        public bool invert = false;

        [Tooltip("Expand the shape outwards by this many pixels, for a glow or shadow plate that " +
                 "sits proud of the art.")]
        public int grow = 0;

        [Tooltip("Keep only the ring the growth added, throwing away the original shape - an outline " +
                 "rather than a fattened silhouette.")]
        public bool outlineOnly = false;

        [Tooltip("Added to the file name of the mask, which is written to the same folder as the " +
                 "source texture.")]
        public string suffix = DefaultSuffix;

        [Tooltip("Copy the source's import settings - pivot, pixels per unit, 9-slice border - onto " +
                 "the mask, so it lines up with the sprite it was made from.")]
        public bool copyImportSettings = true;

        private const string PrefsKey = "Utilities.Editor.SpriteEditor.MaskOptions";
        public const string DefaultSuffix = "-mask";

        public void Validate()
        {
            // 0 would make even a fully empty pixel pass '>= threshold', i.e. a solid
            // rectangle, which is what MaskShape.Everything is for.
            threshold = Mathf.Clamp(threshold, 1, 255);

            // The distance pass is cheap, but a 64px halo on a 64px icon is already all halo.
            grow = Mathf.Clamp(grow, 0, 64);
            suffix = SanitizeSuffix(suffix);
        }

        /// <summary>Keeps the mask's file name legal and never equal to the source's.</summary>
        public static string SanitizeSuffix(string suffix)
        {
            return SpriteImage.SanitizeSuffix(suffix, DefaultSuffix);
        }

        /// <summary>
        /// Where the mask for <paramref name="target"/> goes: same folder, suffix on the name -
        /// beside the asset in the project, or beside the file on disk for one from outside it.
        /// Always .png, because a mask is nothing but a shape held in an alpha channel, which JPEG
        /// cannot store.
        /// </summary>
        public string BuildOutputPath(SpriteTarget target)
        {
            return target.SiblingPath(SanitizeSuffix(suffix), ".png");
        }

        /// <summary>The same file, as somewhere it can actually be written.</summary>
        public string BuildOutputAbsolutePath(SpriteTarget target)
        {
            return target.SiblingAbsolutePath(SanitizeSuffix(suffix), ".png");
        }

        public void Save()
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
        }

        public static MaskOptions Load()
        {
            var options = new MaskOptions();
            string json = EditorPrefs.GetString(PrefsKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(json, options);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"{SpriteImage.Log} Could not read saved mask options, using " +
                                     $"defaults: {exception.Message}");
                    options = new MaskOptions();
                }
            }

            options.Validate();
            return options;
        }
    }
}
