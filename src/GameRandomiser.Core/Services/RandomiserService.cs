using System;
using System.Collections.Generic;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Animation;

namespace GameRandomiser.Core.Services
{
    /// <summary>
    /// The random decision. Deliberately simple: every entry has exactly 1/N probability,
    /// independent of order, history, playtime, install state or anything else.
    /// Winner selection happens BEFORE any animation; the animation is then planned to land on it.
    /// </summary>
    public sealed class RandomiserService
    {
        private readonly IRandomSource random;

        public RandomiserService(IRandomSource random)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
        }

        /// <summary>Uniformly selects an index in [0, count).</summary>
        public int PickWinnerIndex(int count)
        {
            if (count <= 0)
            {
                throw new InvalidOperationException("Cannot pick a winner from an empty wheel.");
            }

            return random.NextInt(count);
        }

        /// <summary>
        /// Picks a winner and computes the rotation that lands it under the pointer.
        /// Step 1 (selection) and step 2 (rotation) are separate so each can be tested alone.
        /// </summary>
        public SpinPlan PlanSpin(int segmentCount, double currentRotation, SpinOptions options)
        {
            var winner = PickWinnerIndex(segmentCount);
            return SpinPlanner.Plan(segmentCount, winner, currentRotation, options, random);
        }

        /// <summary>Unbiased Fisher-Yates shuffle.</summary>
        public void Shuffle<T>(IList<T> items) => ShuffleInPlace(items, random);

        internal static void ShuffleInPlace<T>(IList<T> items, IRandomSource random)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = random.NextInt(i + 1);
                var tmp = items[i];
                items[i] = items[j];
                items[j] = tmp;
            }
        }
    }
}
