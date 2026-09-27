using System;

namespace GameRandomiser.Core.Animation
{
    /// <summary>
    /// Angle conventions shared by the planner and the renderer.
    /// All angles are degrees, measured clockwise from 12 o'clock (screen "up").
    /// Segment i occupies local angles [i * sweep, (i + 1) * sweep).
    /// A wheel rotated by R draws local angle a at screen angle a + R.
    /// </summary>
    public static class WheelGeometry
    {
        /// <summary>Default pointer position: 3 o'clock (right side), pointing into the wheel.</summary>
        public const double DefaultPointerAngle = 90.0;

        public static double SegmentSweep(int segmentCount)
        {
            if (segmentCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount));
            }

            return 360.0 / segmentCount;
        }

        /// <summary>Wraps any angle into [0, 360).</summary>
        public static double Normalize(double degrees)
        {
            var result = degrees % 360.0;
            if (result < 0)
            {
                result += 360.0;
            }

            // Guard against -0.0000001 % 360 + 360 == 360.
            return result >= 360.0 ? 0.0 : result;
        }

        /// <summary>The segment currently under the pointer for a given wheel rotation.</summary>
        public static int IndexUnderPointer(double rotation, int segmentCount, double pointerAngle = DefaultPointerAngle)
        {
            var sweep = SegmentSweep(segmentCount);
            var local = Normalize(pointerAngle - rotation);
            var index = (int)Math.Floor(local / sweep);
            return Math.Min(Math.Max(index, 0), segmentCount - 1);
        }

        /// <summary>
        /// Wheel rotation (in [0, 360)) that places the point <paramref name="fraction"/> of the way
        /// through segment <paramref name="index"/> exactly under the pointer.
        /// </summary>
        public static double RotationForSegment(int index, double fraction, int segmentCount, double pointerAngle = DefaultPointerAngle)
        {
            var sweep = SegmentSweep(segmentCount);
            return Normalize(pointerAngle - (index + fraction) * sweep);
        }

        /// <summary>Centre angle (local) of a segment.</summary>
        public static double SegmentCentre(int index, int segmentCount) => (index + 0.5) * SegmentSweep(segmentCount);
    }
}
