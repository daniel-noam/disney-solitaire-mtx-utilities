using System.Collections.Generic;
using System.Text;

namespace Utilities.Editor
{
    /// <summary>
    /// Turns one written pattern into the run of keys it stands for: "Reward_Panel_{n}" and a count
    /// of 60 becomes Reward_Panel_0 through Reward_Panel_59.
    ///
    /// The slot is written out, so it can go anywhere in the name rather than only at the end -
    /// "Panel_{n}_Reward" is as ordinary as "Reward_Panel_{n}".
    ///
    /// Repeating the n sets the width: {n} counts 0, 1, 2 and {nn} counts 00, 01, 02. The padding
    /// is part of the name rather than a field beside it, so the two cannot fall out of step.
    ///
    /// Deliberately free of any Unity reference: this is the whole of the tool's thinking, and it
    /// can be exercised without an editor.
    /// </summary>
    public static class BindingKeyPattern
    {
        /// <summary>The most keys one press can add, so a mistyped count cannot fill a template.</summary>
        public const int MaxCount = 500;

        /// <summary>
        /// Splits a pattern around its slot. False when there is no slot, which is the one thing
        /// the caller has to say out loud - a pattern without one would silently make the same key
        /// over and over.
        /// </summary>
        public static bool TryParse(string pattern, out string head, out string tail, out int digits)
        {
            head = string.Empty;
            tail = string.Empty;
            digits = 1;

            if (string.IsNullOrEmpty(pattern)) return false;

            var trimmed = pattern.Trim();

            for (var open = trimmed.IndexOf('{'); open >= 0; open = trimmed.IndexOf('{', open + 1))
            {
                var scan = open + 1;
                while (scan < trimmed.Length && trimmed[scan] == 'n') scan++;

                // "{n}" at the very least, and a closing brace right after the run of n's.
                if (scan == open + 1 || scan >= trimmed.Length || trimmed[scan] != '}') continue;

                head = trimmed.Substring(0, open);
                tail = trimmed.Substring(scan + 1);
                digits = scan - open - 1;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an existing key belongs to this pattern's family: the same text either side of
        /// the slot, with nothing but digits between them.
        ///
        /// The width is not checked. A list that has drifted into Panel_5 beside Panel_05 is
        /// exactly the mess worth catching, and treating those as two unrelated keys would leave
        /// half of it behind.
        /// </summary>
        public static bool Matches(string pattern, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!TryParse(pattern, out var head, out var tail, out _)) return false;
            if (key.Length <= head.Length + tail.Length) return false;

            if (!key.StartsWith(head, System.StringComparison.Ordinal)) return false;
            if (!key.EndsWith(tail, System.StringComparison.Ordinal)) return false;

            for (var i = head.Length; i < key.Length - tail.Length; i++)
                if (key[i] < '0' || key[i] > '9') return false;

            return true;
        }

        /// <summary>
        /// Every key the pattern produces, in order. Returns an empty list rather than throwing on
        /// anything nonsensical - this runs per repaint to draw the preview.
        /// </summary>
        public static List<string> Expand(string pattern, int first, int count)
        {
            var keys = new List<string>();

            if (count <= 0 || !TryParse(pattern, out var head, out var tail, out var digits)) return keys;
            if (count > MaxCount) count = MaxCount;

            var builder = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                builder.Length = 0;
                builder.Append(head);
                builder.Append((first + i).ToString().PadLeft(digits, '0'));
                builder.Append(tail);
                keys.Add(builder.ToString());
            }

            return keys;
        }
    }
}
