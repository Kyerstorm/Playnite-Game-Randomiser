using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRandomiser.Core.Layout
{
    /// <summary>Measures text for a specific font. Implemented with WPF FormattedText in the plugin.</summary>
    public interface ITextMeasurer
    {
        double MeasureWidth(string text, double fontSize);

        /// <summary>Height of one line of text at the given size.</summary>
        double LineHeight(double fontSize);
    }

    public sealed class LabelRequest
    {
        public string Text { get; set; }

        /// <summary>Angular width of the segment in degrees.</summary>
        public double SweepDegrees { get; set; }

        /// <summary>Radius where text may start (e.g. hub edge, or just outside an icon).</summary>
        public double InnerRadius { get; set; }

        /// <summary>Radius where text must end (just inside the rim, or inside a cover thumbnail).</summary>
        public double OuterRadius { get; set; }

        public double MaxFontSize { get; set; } = 18;
        public double MinFontSize { get; set; } = 7;
        public int MaxLines { get; set; } = 3;
    }

    public sealed class LabelLayout
    {
        public static readonly LabelLayout Hidden = new LabelLayout(new string[0], 0, false, false, 0);

        public LabelLayout(IReadOnlyList<string> lines, double fontSize, bool isVisible, bool isTruncated, double width)
        {
            Lines = lines;
            FontSize = fontSize;
            IsVisible = isVisible;
            IsTruncated = isTruncated;
            Width = width;
        }

        public IReadOnlyList<string> Lines { get; }
        public double FontSize { get; }
        public bool IsVisible { get; }
        public bool IsTruncated { get; }

        /// <summary>Width of the widest line at <see cref="FontSize"/>.</summary>
        public double Width { get; }
    }

    /// <summary>
    /// Fits a game name radially inside a wedge-shaped segment.
    /// Text runs along the segment's centre line, reading outward toward the rim (Wheel-of-Names style).
    /// The usable "height" of a line block shrinks toward the hub because the wedge narrows, so the engine
    /// tries 1..MaxLines balanced wraps and picks whichever allows the largest font. If even the minimum
    /// font does not fit, it falls back to a single ellipsised line, or hides the label entirely.
    /// </summary>
    public sealed class LabelLayoutEngine
    {
        /// <summary>Fraction of the local wedge thickness text may occupy (leaves breathing room).</summary>
        private const double ThicknessFill = 0.82;
        private const string Ellipsis = "…";

        private readonly ITextMeasurer measurer;

        public LabelLayoutEngine(ITextMeasurer measurer)
        {
            this.measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));
        }

        /// <summary>Wedge thickness (chord) available at a radius.</summary>
        public static double ThicknessAt(double radius, double sweepDegrees)
        {
            if (radius <= 0)
            {
                return 0;
            }

            if (sweepDegrees >= 180)
            {
                // Half-wheel or more: effectively the whole diameter is available.
                return 2 * radius;
            }

            return 2 * radius * Math.Sin(sweepDegrees * Math.PI / 360.0);
        }

        public LabelLayout Layout(LabelRequest request)
        {
            var text = (request.Text ?? string.Empty).Trim();
            var length = request.OuterRadius - request.InnerRadius;
            if (text.Length == 0 || length <= 0 || request.SweepDegrees <= 0)
            {
                return LabelLayout.Hidden;
            }

            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var maxLines = Math.Max(1, Math.Min(request.MaxLines, words.Length));

            LabelLayout best = null;
            for (var lineCount = 1; lineCount <= maxLines; lineCount++)
            {
                var lines = WrapBalanced(words, lineCount, request.MaxFontSize);
                var size = FitFont(lines, request, length);
                if (size < request.MinFontSize)
                {
                    continue;
                }

                // Prefer fewer lines unless wrapping buys a clearly bigger font (>12%).
                if (best == null || size > best.FontSize * 1.12)
                {
                    best = new LabelLayout(lines, size, true, false, WidestLine(lines, size));
                }
            }

            if (best != null)
            {
                return best;
            }

            return Truncated(text, request, length);
        }

        private double FitFont(IReadOnlyList<string> lines, LabelRequest request, double length)
        {
            var size = request.MaxFontSize;

            // Fit the radial length first (width scales linearly with font size).
            var widthAtMax = WidestLine(lines, size);
            if (widthAtMax > length)
            {
                size *= length / widthAtMax;
            }

            // Then shrink until the line block fits the wedge thickness where the text begins.
            for (var i = 0; i < 8; i++)
            {
                var width = WidestLine(lines, size);
                var textInner = Math.Max(request.InnerRadius, request.OuterRadius - width);
                var available = ThicknessAt(textInner, request.SweepDegrees) * ThicknessFill;
                var blockHeight = measurer.LineHeight(size) * lines.Count;
                if (blockHeight <= available + 0.01)
                {
                    break;
                }

                size *= Math.Max(0.5, available / blockHeight);
            }

            return size;
        }

        private LabelLayout Truncated(string text, LabelRequest request, double length)
        {
            var size = request.MinFontSize;

            // The wedge narrows toward the hub, so a line may only extend inward to the radius where
            // one line at the minimum font still fits. Solve 2 r sin(sweep/2) * fill = lineHeight for r.
            var lineHeight = measurer.LineHeight(size);
            var minRadius = request.SweepDegrees >= 180
                ? lineHeight / (2 * ThicknessFill)
                : lineHeight / (2 * Math.Sin(request.SweepDegrees * Math.PI / 360.0) * ThicknessFill);
            length = request.OuterRadius - Math.Max(request.InnerRadius, minRadius);
            if (length < size * 2)
            {
                // Segment too thin for any readable text: hide rather than render an unreadable smear.
                return LabelLayout.Hidden;
            }

            if (measurer.MeasureWidth(text, size) <= length)
            {
                return new LabelLayout(new[] { text }, size, true, false, measurer.MeasureWidth(text, size));
            }

            // Binary search the longest prefix that fits with an ellipsis.
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (measurer.MeasureWidth(text.Substring(0, mid).TrimEnd() + Ellipsis, size) <= length)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (lo == 0)
            {
                return LabelLayout.Hidden;
            }

            var truncated = text.Substring(0, lo).TrimEnd() + Ellipsis;
            return new LabelLayout(new[] { truncated }, size, true, true, measurer.MeasureWidth(truncated, size));
        }

        private double WidestLine(IReadOnlyList<string> lines, double size) =>
            lines.Count == 0 ? 0 : lines.Max(l => measurer.MeasureWidth(l, size));

        /// <summary>Splits words into exactly <paramref name="lineCount"/> lines minimising the widest line.</summary>
        internal IReadOnlyList<string> WrapBalanced(string[] words, int lineCount, double fontSize)
        {
            if (lineCount <= 1 || words.Length <= 1)
            {
                return new[] { string.Join(" ", words) };
            }

            if (lineCount >= words.Length)
            {
                return words;
            }

            // Words are few (game titles), so an exhaustive search over break positions is cheap.
            // Cap the search for pathological titles and fall back to greedy.
            if (words.Length > 14)
            {
                return WrapGreedy(words, lineCount, fontSize);
            }

            string[] bestLines = null;
            var bestWidth = double.MaxValue;
            var breaks = new int[lineCount - 1];
            Search(0, 1);
            return bestLines;

            void Search(int breakIndex, int start)
            {
                if (breakIndex == breaks.Length)
                {
                    var lines = BuildLines(words, breaks);
                    var width = lines.Max(l => measurer.MeasureWidth(l, fontSize));
                    if (width < bestWidth)
                    {
                        bestWidth = width;
                        bestLines = lines;
                    }

                    return;
                }

                var remainingBreaks = breaks.Length - breakIndex - 1;
                for (var position = start; position <= words.Length - 1 - remainingBreaks; position++)
                {
                    breaks[breakIndex] = position;
                    Search(breakIndex + 1, position + 1);
                }
            }
        }

        private IReadOnlyList<string> WrapGreedy(string[] words, int lineCount, double fontSize)
        {
            var total = measurer.MeasureWidth(string.Join(" ", words), fontSize);
            var target = total / lineCount;
            var lines = new List<string>();
            var current = new List<string>();
            foreach (var word in words)
            {
                current.Add(word);
                if (lines.Count < lineCount - 1 && measurer.MeasureWidth(string.Join(" ", current), fontSize) >= target)
                {
                    lines.Add(string.Join(" ", current));
                    current.Clear();
                }
            }

            if (current.Count > 0)
            {
                lines.Add(string.Join(" ", current));
            }

            return lines;
        }

        private static string[] BuildLines(string[] words, int[] breaks)
        {
            var lines = new string[breaks.Length + 1];
            var start = 0;
            for (var i = 0; i <= breaks.Length; i++)
            {
                var end = i < breaks.Length ? breaks[i] : words.Length;
                lines[i] = string.Join(" ", words, start, end - start);
                start = end;
            }

            return lines;
        }
    }
}
