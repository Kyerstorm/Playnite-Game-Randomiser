using System;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Animation
{
    public sealed class SpinOptions
    {
        public const double MinDurationSeconds = 2.0;
        public const double MaxDurationSeconds = 10.0;

        public double DurationSeconds { get; set; } = 5.0;
        public SpinIntensity Intensity { get; set; } = SpinIntensity.Normal;
        public double PointerAngle { get; set; } = WheelGeometry.DefaultPointerAngle;

        /// <summary>One-item wheels get a short, token spin instead of a full animation.</summary>
        public double SingleEntryDurationSeconds { get; set; } = 1.2;

        public SpinEasing Easing { get; set; } = SpinEasing.Default;
    }

    /// <summary>An immutable, fully determined spin: where it starts, where it ends, how long it takes.</summary>
    public sealed class SpinPlan
    {
        public SpinPlan(int segmentCount, int winnerIndex, double startRotation, double endRotation, double durationSeconds, double pointerAngle, SpinEasing easing)
        {
            SegmentCount = segmentCount;
            WinnerIndex = winnerIndex;
            StartRotation = startRotation;
            EndRotation = endRotation;
            DurationSeconds = durationSeconds;
            PointerAngle = pointerAngle;
            Easing = easing ?? SpinEasing.Default;
        }

        public int SegmentCount { get; }
        public int WinnerIndex { get; }
        public double StartRotation { get; }
        public double EndRotation { get; }
        public double DurationSeconds { get; }
        public double PointerAngle { get; }
        public SpinEasing Easing { get; }

        public double TotalDegrees => EndRotation - StartRotation;

        /// <summary>Wheel rotation at a given elapsed time. Exactly EndRotation once complete.</summary>
        public double RotationAt(double elapsedSeconds)
        {
            if (elapsedSeconds >= DurationSeconds)
            {
                return EndRotation;
            }

            var t = DurationSeconds <= 0 ? 1 : elapsedSeconds / DurationSeconds;
            return StartRotation + TotalDegrees * Easing.Evaluate(t);
        }

        public bool IsComplete(double elapsedSeconds) => elapsedSeconds >= DurationSeconds;
    }

    /// <summary>
    /// Step 2 of a spin: given an already-chosen winner, compute the rotation that lands it under the pointer.
    /// </summary>
    public static class SpinPlanner
    {
        /// <summary>Stop somewhere in the middle 60% of the winning segment, never on a boundary.</summary>
        public const double MinLandingFraction = 0.2;
        public const double MaxLandingFraction = 0.8;

        public static double TurnsPerSecond(SpinIntensity intensity)
        {
            switch (intensity)
            {
                case SpinIntensity.Gentle: return 0.7;
                case SpinIntensity.Wild: return 1.8;
                default: return 1.15;
            }
        }

        public static SpinPlan Plan(int segmentCount, int winnerIndex, double currentRotation, SpinOptions options, IRandomSource random)
        {
            if (segmentCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount));
            }

            if (winnerIndex < 0 || winnerIndex >= segmentCount)
            {
                throw new ArgumentOutOfRangeException(nameof(winnerIndex));
            }

            options = options ?? new SpinOptions();
            var duration = Clamp(options.DurationSeconds, SpinOptions.MinDurationSeconds, SpinOptions.MaxDurationSeconds);

            // The landing offset is cosmetic (it makes stops look natural) and has no effect on who wins.
            var fraction = segmentCount == 1
                ? 0.5
                : MinLandingFraction + (MaxLandingFraction - MinLandingFraction) * random.NextDouble();

            var target = WheelGeometry.RotationForSegment(winnerIndex, fraction, segmentCount, options.PointerAngle);
            var forward = WheelGeometry.Normalize(target - WheelGeometry.Normalize(currentRotation));

            int extraTurns;
            if (segmentCount == 1)
            {
                duration = Math.Min(duration, options.SingleEntryDurationSeconds);
                extraTurns = 1;
            }
            else
            {
                extraTurns = Math.Max(2, (int)Math.Round(duration * TurnsPerSecond(options.Intensity)));
            }

            var end = currentRotation + forward + 360.0 * extraTurns;
            return new SpinPlan(segmentCount, winnerIndex, currentRotation, end, duration, options.PointerAngle, options.Easing);
        }

        private static double Clamp(double value, double min, double max) =>
            double.IsNaN(value) ? min : Math.Max(min, Math.Min(max, value));
    }
}
