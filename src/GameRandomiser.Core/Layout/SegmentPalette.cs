using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameRandomiser.Core.Layout
{
    /// <summary>Segment colour cycling and colour maths (contrast), independent of WPF.</summary>
    public static class SegmentPalette
    {
        /// <summary>Vivid, mutually distinguishable defaults that work on light and dark backgrounds.</summary>
        public static readonly IReadOnlyList<string> DefaultColours = new[]
        {
            "#3369E8", // blue
            "#D50F25", // red
            "#EEB211", // amber
            "#009925", // green
            "#8E44AD", // purple
            "#F26B1D", // orange
            "#16A5A5", // teal
            "#D63384"  // pink
        };

        public const string DefaultAccent = "#1E88E5";

        /// <summary>
        /// Palette index for a segment, cycling through the palette while guaranteeing the last
        /// segment never matches its neighbour across the wrap-around seam (when avoidable).
        /// </summary>
        public static int ColourIndexFor(int segmentIndex, int segmentCount, int paletteSize)
        {
            if (paletteSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(paletteSize));
            }

            var index = segmentIndex % paletteSize;
            var isLast = segmentIndex == segmentCount - 1;
            if (isLast && segmentCount > 1 && paletteSize >= 3 && index == 0)
            {
                // Last would equal the first segment's colour; pick one that differs from both neighbours.
                // Previous is (paletteSize - 1), first is 0, so 1 is always distinct when paletteSize >= 3.
                return 1;
            }

            return index;
        }

        public static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return false;
            }

            var s = hex.Trim().TrimStart('#');
            if (s.Length == 3)
            {
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            }

            if (s.Length == 8)
            {
                s = s.Substring(2); // ARGB -> ignore alpha
            }

            if (s.Length != 6)
            {
                return false;
            }

            return byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

        /// <summary>WCAG relative luminance, 0 (black) .. 1 (white).</summary>
        public static double RelativeLuminance(byte r, byte g, byte b)
        {
            double Channel(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        public static double ContrastRatio(double luminanceA, double luminanceB)
        {
            var lighter = Math.Max(luminanceA, luminanceB);
            var darker = Math.Min(luminanceA, luminanceB);
            return (lighter + 0.05) / (darker + 0.05);
        }

        /// <summary>True if black text reads better than white text on this background.</summary>
        public static bool PrefersDarkText(byte r, byte g, byte b)
        {
            var l = RelativeLuminance(r, g, b);
            return ContrastRatio(l, 0) > ContrastRatio(l, 1);
        }
    }
}
