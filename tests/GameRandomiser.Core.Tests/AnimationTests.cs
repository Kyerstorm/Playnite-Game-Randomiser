using System;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Models;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    public class AnimationTests
    {
        [Fact]
        public void Easing_StartsAtZero_EndsAtOne()
        {
            var easing = SpinEasing.Default;
            Assert.Equal(0, easing.Evaluate(0));
            Assert.Equal(1, easing.Evaluate(1));
            Assert.Equal(0, easing.Velocity(1));
            Assert.Equal(0, easing.Velocity(0));
        }

        [Fact]
        public void Easing_IsMonotonic_AndContinuous()
        {
            var easing = SpinEasing.Default;
            var previous = 0.0;
            const int steps = 10_000;
            for (var i = 1; i <= steps; i++)
            {
                var value = easing.Evaluate(i / (double)steps);
                Assert.True(value >= previous - 1e-12, $"Not monotonic at step {i}");
                // No jump bigger than peak velocity * dt (+ tolerance): no snapping anywhere.
                Assert.True(value - previous < 3.0 / steps, $"Discontinuity at step {i}");
                previous = value;
            }
        }

        [Fact]
        public void Easing_Accelerates_Cruises_ThenDecelerates()
        {
            var easing = SpinEasing.Default;
            Assert.True(easing.Velocity(0.02) < easing.Velocity(0.07));   // accelerating
            Assert.Equal(easing.Velocity(0.1), easing.Velocity(0.25), 10); // sustained top speed
            Assert.True(easing.Velocity(0.5) < easing.Velocity(0.3));     // decelerating
            Assert.True(easing.Velocity(0.95) < easing.Velocity(0.8));
            Assert.True(easing.Velocity(0.999) < 0.001);                  // gentle final crawl
        }

        [Fact]
        public void Geometry_IndexUnderPointer_MatchesSegmentLayout()
        {
            // 4 segments, pointer at 90deg (right), no rotation: local 90deg is the start of segment 1.
            Assert.Equal(1, WheelGeometry.IndexUnderPointer(0, 4));
            Assert.Equal(0, WheelGeometry.IndexUnderPointer(45, 4));
            Assert.Equal(3, WheelGeometry.IndexUnderPointer(135, 4));
            Assert.Equal(1, WheelGeometry.IndexUnderPointer(-45, 4));
            Assert.Equal(2, WheelGeometry.IndexUnderPointer(-135, 4));
        }

        [Theory]
        [InlineData(-720.5, 359.5)]
        [InlineData(360, 0)]
        [InlineData(725, 5)]
        [InlineData(-0.0000000001, 359.9999999999)]
        public void Geometry_Normalize(double input, double expected)
        {
            Assert.Equal(expected, WheelGeometry.Normalize(input), 6);
        }

        /// <summary>The key guarantee: every spin visually lands on the pre-selected winner.</summary>
        [Fact]
        public void EverySpin_LandsOnWinner_AcrossSizesDurationsAndStartAngles()
        {
            var random = new SeededRandomSource(2026);
            var sizes = new[] { 1, 2, 3, 5, 7, 12, 20, 37, 50, 99, 100, 250, 500, 1000 };
            var durations = new[] { 2.0, 3.5, 5.0, 7.25, 10.0 };
            var intensities = new[] { SpinIntensity.Gentle, SpinIntensity.Normal, SpinIntensity.Wild };
            var checks = 0;

            foreach (var n in sizes)
            {
                foreach (var duration in durations)
                {
                    foreach (var intensity in intensities)
                    {
                        var rotation = random.NextDouble() * 10_000 - 5_000;
                        for (var trial = 0; trial < 6; trial++)
                        {
                            var winner = random.NextInt(n);
                            var options = new SpinOptions { DurationSeconds = duration, Intensity = intensity };
                            var plan = SpinPlanner.Plan(n, winner, rotation, options, random);

                            Assert.Equal(winner, WheelGeometry.IndexUnderPointer(plan.EndRotation, n, plan.PointerAngle));
                            Assert.Equal(plan.EndRotation, plan.RotationAt(plan.DurationSeconds));
                            Assert.Equal(plan.EndRotation, plan.RotationAt(plan.DurationSeconds + 5));
                            Assert.True(plan.EndRotation > plan.StartRotation, "Wheel must always spin forward");

                            // Landing is well inside the segment, never on a boundary.
                            var local = WheelGeometry.Normalize(plan.PointerAngle - plan.EndRotation);
                            var within = local / WheelGeometry.SegmentSweep(n) - winner;
                            Assert.InRange(within, 0.19, 0.81);

                            rotation = plan.EndRotation; // next spin continues from where this one stopped
                            checks++;
                        }
                    }
                }
            }

            Assert.True(checks > 1000);
        }

        [Fact]
        public void Spin_HasSubstantialRotation_ScaledByDuration()
        {
            var random = new SeededRandomSource(1);
            var shortSpin = SpinPlanner.Plan(10, 3, 0, new SpinOptions { DurationSeconds = 2 }, random);
            var longSpin = SpinPlanner.Plan(10, 3, 0, new SpinOptions { DurationSeconds = 10 }, random);

            Assert.True(shortSpin.TotalDegrees >= 720, "At least two full turns");
            Assert.True(longSpin.TotalDegrees > shortSpin.TotalDegrees);
            Assert.Equal(2, shortSpin.DurationSeconds);
            Assert.Equal(10, longSpin.DurationSeconds);
        }

        [Theory]
        [InlineData(0.5, 2)]
        [InlineData(60, 10)]
        [InlineData(double.NaN, 2)]
        public void Spin_DurationIsClamped(double requested, double expected)
        {
            var plan = SpinPlanner.Plan(10, 0, 0, new SpinOptions { DurationSeconds = requested }, new SeededRandomSource(1));
            Assert.Equal(expected, plan.DurationSeconds);
        }

        [Fact]
        public void Spin_RotationIsMonotonicOverTime()
        {
            var plan = SpinPlanner.Plan(24, 5, 123, new SpinOptions { DurationSeconds = 5 }, new SeededRandomSource(3));
            var previous = plan.RotationAt(0);
            Assert.Equal(123, previous);
            for (var t = 0.0; t <= 5.0; t += 1 / 144.0)
            {
                var r = plan.RotationAt(t);
                Assert.True(r >= previous);
                previous = r;
            }
        }

        [Fact]
        public void Planner_RejectsInvalidInput()
        {
            var random = new SeededRandomSource(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => SpinPlanner.Plan(0, 0, 0, null, random));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpinPlanner.Plan(5, 5, 0, null, random));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpinPlanner.Plan(5, -1, 0, null, random));
        }
    }
}
