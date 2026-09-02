using UnityEngine;

namespace Tools.Editor.EditorUtilities
{
    /// <summary>
    /// Finds the stretchable band of a texture - the run of identical rows/columns a 9-slice border
    /// should leave out - and builds the collapsed version of the image.
    /// </summary>
    public static class NineSliceAnalyzer
    {
        // ---------------------------------------------------------------------------------------
        // Detection
        // ---------------------------------------------------------------------------------------

        public static NineSliceDetectionResult Detect(SpriteSnapshot snapshot, NineSliceOptions options)
        {
            var result = new NineSliceDetectionResult { Border = NineSliceBorder.Zero, Message = string.Empty };
            if (snapshot == null) return result;

            options.Validate();

            int left = 0;
            int right = 0;
            int bottom = 0;
            int top = 0;

            if (options.detectHorizontal && snapshot.Width > 1 &&
                TryFindBand(snapshot, options, true, out int xStart, out int xEnd))
            {
                left = xStart;
                right = snapshot.Width - 1 - xEnd;
                result.FoundHorizontal = true;
            }

            if (options.detectVertical && snapshot.Height > 1 &&
                TryFindBand(snapshot, options, false, out int yStart, out int yEnd))
            {
                bottom = yStart;
                top = snapshot.Height - 1 - yEnd;
                result.FoundVertical = true;
            }

            var border = new NineSliceBorder(left, bottom, right, top);
            if (options.symmetricBorders) border = border.Symmetrical();
            result.Border = border.Clamped(snapshot.Width, snapshot.Height);
            result.Message = Describe(result, options);
            return result;
        }

        private static string Describe(NineSliceDetectionResult result, NineSliceOptions options)
        {
            if (result.FoundHorizontal && result.FoundVertical) return $"Border {result.Border}.";

            string missing;
            if (!result.FoundHorizontal && !result.FoundVertical) missing = "either axis";
            else if (!result.FoundHorizontal) missing = "the X axis";
            else missing = "the Y axis";

            bool disabled = (!options.detectHorizontal && !result.FoundHorizontal) ||
                            (!options.detectVertical && !result.FoundVertical);
            if (disabled) return $"Border {result.Border} (detection off for {missing}).";

            return $"Border {result.Border} - no repeating band on {missing}. " +
                   "Raise the tolerance, lower the margin, or drag the guides by hand.";
        }

        /// <summary>
        /// Finds the longest run of neighbouring lines that match, then trims the margin off both
        /// ends. <paramref name="start"/> and <paramref name="end"/> are inclusive line indices of
        /// the resulting stretchable band.
        /// </summary>
        private static bool TryFindBand(SpriteSnapshot snapshot, NineSliceOptions options, bool horizontal,
            out int start, out int end)
        {
            start = 0;
            end = 0;

            int lineCount = horizontal ? snapshot.Width : snapshot.Height;
            int lineLength = horizontal ? snapshot.Height : snapshot.Width;

            // A run of matches at indices [a..b] means lines a-1 through b are all alike, so the
            // band itself starts one line earlier than the first match.
            int bestStart = 0;
            int bestEnd = -1;
            int runStart = -1;

            for (int i = 1; i < lineCount; i++)
            {
                if (!LinesMatch(snapshot, options, horizontal, i, lineLength))
                {
                    runStart = -1;
                    continue;
                }

                if (runStart < 0) runStart = i - 1;
                if (i - runStart > bestEnd - bestStart)
                {
                    bestStart = runStart;
                    bestEnd = i;
                }
            }

            if (bestEnd < bestStart) return false;

            start = bestStart + options.margin;
            end = bestEnd - options.margin;

            // The margin ate the whole band: there is nothing left to stretch.
            return end >= start;
        }

        private static bool LinesMatch(SpriteSnapshot snapshot, NineSliceOptions options, bool horizontal,
            int index, int lineLength)
        {
            var pixels = snapshot.Pixels;
            int width = snapshot.Width;
            bool ignoreTransparent = options.ignoreTransparentColor;
            bool average = options.comparison == NineSliceComparison.AverageDifference;
            int outliers = 0;
            long total = 0;

            for (int k = 0; k < lineLength; k++)
            {
                int current = horizontal ? k * width + index : index * width + k;
                int previous = horizontal ? k * width + index - 1 : (index - 1) * width + k;

                int difference = MaxChannelDifference(
                    Normalize(pixels[current], ignoreTransparent),
                    Normalize(pixels[previous], ignoreTransparent));

                if (average)
                {
                    total += difference;
                    continue;
                }

                if (difference <= options.tolerance) continue;
                if (++outliers > options.allowedOutliers) return false;
            }

            // mean <= tolerance, without the division.
            return !average || total <= (long) options.tolerance * lineLength;
        }

        /// <summary>
        /// Zeroes the colour behind fully transparent pixels so that two transparent regions compare
        /// equal even when the painting tool left different RGB values there.
        /// </summary>
        private static Color32 Normalize(Color32 color, bool ignoreTransparentColor)
        {
            return ignoreTransparentColor && color.a == 0 ? new Color32(0, 0, 0, 0) : color;
        }

        private static int MaxChannelDifference(Color32 a, Color32 b)
        {
            int difference = Mathf.Abs(a.r - b.r);
            difference = Mathf.Max(difference, Mathf.Abs(a.g - b.g));
            difference = Mathf.Max(difference, Mathf.Abs(a.b - b.b));
            return Mathf.Max(difference, Mathf.Abs(a.a - b.a));
        }

        // ---------------------------------------------------------------------------------------
        // Compression
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Copy of the texture with its stretchable centre collapsed to <paramref name="centerSize"/>
        /// pixels on each axis. The border pixels are untouched, so the border values stay valid.
        /// Caller owns the returned texture.
        /// </summary>
        public static Texture2D CreateCompressed(SpriteSnapshot snapshot, NineSliceBorder border, int centerSize)
        {
            centerSize = Mathf.Max(1, centerSize);
            var columns = BuildAxisMap(snapshot.Width, border.left, border.right, centerSize);
            var rows = BuildAxisMap(snapshot.Height, border.bottom, border.top, centerSize);

            var source = snapshot.Pixels;
            var output = new Color32[columns.Length * rows.Length];
            for (int y = 0; y < rows.Length; y++)
            {
                int sourceRow = rows[y] * snapshot.Width;
                int targetRow = y * columns.Length;
                for (int x = 0; x < columns.Length; x++) output[targetRow + x] = source[sourceRow + columns[x]];
            }

            var texture = new Texture2D(columns.Length, rows.Length, TextureFormat.RGBA32, false);
            texture.SetPixels32(output);
            texture.Apply(false);
            return texture;
        }

        public static Vector2Int PredictCompressedSize(int width, int height, NineSliceBorder border, int centerSize)
        {
            centerSize = Mathf.Max(1, centerSize);
            return new Vector2Int(
                AxisSize(width, border.left, border.right, centerSize),
                AxisSize(height, border.bottom, border.top, centerSize));
        }

        private static int AxisSize(int size, int near, int far, int centerSize)
        {
            if (near + far == 0 || size - near - far <= centerSize) return size;
            return near + centerSize + far;
        }

        /// <summary>
        /// Source index for every pixel of the collapsed axis. Built explicitly so the copy loop
        /// cannot run off the end of the source, whatever the border and centre size are.
        /// </summary>
        private static int[] BuildAxisMap(int size, int near, int far, int centerSize)
        {
            // Either the axis has no border at all, or its centre is already at/below the target.
            if (AxisSize(size, near, far, centerSize) == size)
            {
                var identity = new int[size];
                for (int i = 0; i < size; i++) identity[i] = i;
                return identity;
            }

            var map = new int[near + centerSize + far];
            for (int i = 0; i < near; i++) map[i] = i;

            // Every pixel of the band is (within tolerance) the same, so the first few stand in for
            // all of them.
            for (int i = 0; i < centerSize; i++) map[near + i] = near + i;
            for (int i = 0; i < far; i++) map[near + centerSize + i] = size - far + i;
            return map;
        }
    }
}
