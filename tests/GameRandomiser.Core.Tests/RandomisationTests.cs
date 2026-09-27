using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Services;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    public class RandomisationTests
    {
        /// <summary>
        /// Chi-squared goodness-of-fit against a uniform distribution.
        /// Critical value for df=19 at p=0.001 is 43.82; a fair picker should essentially never exceed it.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryGame_HasEqualProbability(bool useCrypto)
        {
            const int games = 20;
            const int draws = 200_000;
            IRandomSource source = useCrypto ? (IRandomSource)new CryptoRandomSource() : new SeededRandomSource(1234);
            var randomiser = new RandomiserService(source);
            var counts = new int[games];

            for (var i = 0; i < draws; i++)
            {
                counts[randomiser.PickWinnerIndex(games)]++;
            }

            var expected = (double)draws / games;
            var chiSquared = counts.Sum(c => (c - expected) * (c - expected) / expected);
            Assert.True(chiSquared < 43.82, $"Chi-squared {chiSquared:0.00} suggests a non-uniform picker. Counts: {string.Join(",", counts)}");

            // And each game is within 3% of the ideal 5%.
            foreach (var c in counts)
            {
                Assert.InRange(c / (double)draws, 0.05 * 0.97, 0.05 * 1.03);
            }
        }

        [Fact]
        public void CryptoSource_NeverOutOfRange()
        {
            var source = new CryptoRandomSource();
            foreach (var max in new[] { 1, 2, 3, 7, 100, 1000 })
            {
                for (var i = 0; i < 2000; i++)
                {
                    Assert.InRange(source.NextInt(max), 0, max - 1);
                }
            }

            for (var i = 0; i < 2000; i++)
            {
                var d = source.NextDouble();
                Assert.True(d >= 0 && d < 1);
            }
        }

        [Fact]
        public void ConsecutiveWinners_AreAllowed()
        {
            var randomiser = new RandomiserService(new SeededRandomSource(7));
            var previous = -1;
            var repeats = 0;
            for (var i = 0; i < 1000; i++)
            {
                var winner = randomiser.PickWinnerIndex(3);
                if (winner == previous)
                {
                    repeats++;
                }

                previous = winner;
            }

            // With 3 games we expect ~1/3 of consecutive pairs to repeat.
            Assert.InRange(repeats, 250, 420);
        }

        [Fact]
        public void EmptyWheel_CannotBeSpun()
        {
            var randomiser = new RandomiserService(new SeededRandomSource(1));
            Assert.Throws<InvalidOperationException>(() => randomiser.PickWinnerIndex(0));
        }

        [Fact]
        public void OneGameWheel_AlwaysWins_WithShortSpin()
        {
            var randomiser = new RandomiserService(new SeededRandomSource(1));
            var options = new SpinOptions { DurationSeconds = 8 };
            for (var i = 0; i < 20; i++)
            {
                var plan = randomiser.PlanSpin(1, i * 37.0, options);
                Assert.Equal(0, plan.WinnerIndex);
                Assert.True(plan.DurationSeconds <= options.SingleEntryDurationSeconds);
                Assert.Equal(0, WheelGeometry.IndexUnderPointer(plan.EndRotation, 1));
            }
        }

        [Fact]
        public void Shuffle_IsAPermutation_AndIsUnbiased()
        {
            var randomiser = new RandomiserService(new SeededRandomSource(99));
            // Position of item 0 after shuffling 4 items should be uniform over 4 positions.
            var positions = new int[4];
            for (var i = 0; i < 40_000; i++)
            {
                var items = new List<int> { 0, 1, 2, 3 };
                randomiser.Shuffle(items);
                Assert.Equal(new[] { 0, 1, 2, 3 }, items.OrderBy(x => x));
                positions[items.IndexOf(0)]++;
            }

            foreach (var p in positions)
            {
                Assert.InRange(p, 9_400, 10_600);
            }
        }

        [Fact]
        public void Probability_IsIndependentOfSortMode()
        {
            // Winner selection is index-based over however many entries exist; arrangement only changes
            // which game sits at an index. Verify: after many shuffles, each game still wins ~1/N.
            var catalog = new FakeCatalog();
            var ids = Enumerable.Range(0, 5).Select(i => catalog.Add("G" + i).Id).ToList();
            var service = Build.Service(catalog, seed: 5);
            var wheel = service.CreateWheel("W", ids);
            var randomiser = new RandomiserService(new SeededRandomSource(11));
            var wins = ids.ToDictionary(id => id, id => 0);

            for (var i = 0; i < 50_000; i++)
            {
                if (i % 100 == 0)
                {
                    service.SetSortMode(wheel.Id, (SortMode)(i / 100 % 3));
                }

                var entries = service.ResolveEntries(wheel.Id);
                wins[entries[randomiser.PickWinnerIndex(entries.Count)].Id]++;
            }

            foreach (var w in wins.Values)
            {
                Assert.InRange(w / 50_000.0, 0.19, 0.21);
            }
        }
    }
}
