using System;

namespace GameRandomiser.Core.Animation
{
    /// <summary>
    /// Maps normalised time t in [0,1] to normalised distance in [0,1] using a physically
    /// motivated velocity profile:
    ///   accelerate linearly  (0 .. a)      - the "push"
    ///   cruise at top speed  (a .. b)      - sustained high speed
    ///   decelerate as (1-s)^n (b .. 1)     - friction-like slowdown to exactly zero velocity
    /// Position is the analytic integral of velocity, so it is continuous, monotonic, and reaches
    /// exactly 1.0 with zero velocity: the wheel never snaps at the end.
    /// </summary>
    public sealed class SpinEasing
    {
        public static readonly SpinEasing Default = new SpinEasing();

        private readonly double accelEnd;
        private readonly double cruiseEnd;
        private readonly double decelPower;
        private readonly double peakVelocity;

        public SpinEasing(double accelEnd = 0.08, double cruiseEnd = 0.28, double decelPower = 2.6)
        {
            if (accelEnd <= 0 || cruiseEnd < accelEnd || cruiseEnd >= 1 || decelPower <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(accelEnd), "Require 0 < accelEnd <= cruiseEnd < 1 and decelPower > 0.");
            }

            this.accelEnd = accelEnd;
            this.cruiseEnd = cruiseEnd;
            this.decelPower = decelPower;

            // Total area under the velocity curve must equal 1.
            var area = accelEnd / 2 + (cruiseEnd - accelEnd) + (1 - cruiseEnd) / (decelPower + 1);
            peakVelocity = 1.0 / area;
        }

        /// <summary>Normalised distance travelled at normalised time t.</summary>
        public double Evaluate(double t)
        {
            if (t <= 0)
            {
                return 0;
            }

            if (t >= 1)
            {
                return 1;
            }

            var v = peakVelocity;
            if (t < accelEnd)
            {
                return v * t * t / (2 * accelEnd);
            }

            var afterAccel = v * accelEnd / 2;
            if (t < cruiseEnd)
            {
                return afterAccel + v * (t - accelEnd);
            }

            var afterCruise = afterAccel + v * (cruiseEnd - accelEnd);
            var span = 1 - cruiseEnd;
            var s = (t - cruiseEnd) / span;
            var remaining = Math.Pow(1 - s, decelPower + 1);
            var value = afterCruise + v * span / (decelPower + 1) * (1 - remaining);
            return Math.Min(1.0, value);
        }

        /// <summary>Normalised velocity (d distance / d t) at time t. Zero at both ends.</summary>
        public double Velocity(double t)
        {
            if (t <= 0 || t >= 1)
            {
                return 0;
            }

            if (t < accelEnd)
            {
                return peakVelocity * t / accelEnd;
            }

            if (t < cruiseEnd)
            {
                return peakVelocity;
            }

            var s = (t - cruiseEnd) / (1 - cruiseEnd);
            return peakVelocity * Math.Pow(1 - s, decelPower);
        }
    }
}
