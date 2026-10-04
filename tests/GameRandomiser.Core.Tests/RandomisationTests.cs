using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Diagnostics;
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

    /// <summary>Reroll protection: the eligibility stage in front of the fair pick.</summary>
    public class RerollProtectionTests
    {
        private readonly FakeCatalog catalog = new FakeCatalog();
        private readonly FixedClock clock = new FixedClock();
        private readonly InMemoryStore store = new InMemoryStore();
        private readonly WheelService wheels;
        private readonly RerollProtectionService protection;
        private readonly List<GameInfo> games;
        private readonly RandomiserWheel wheel;

        public RerollProtectionTests()
        {
            games = Enumerable.Range(0, 5).Select(i => catalog.Add("Game " + i)).ToList();
            wheels = Build.Service(catalog, store, clock: clock);
            protection = new RerollProtectionService(wheels);
            wheel = wheels.CreateWheel("Wheel", games.Select(g => g.Id));
        }

        private static RerollProtectionOptions Options(Action<RerollProtectionOptions> configure = null)
        {
            var options = new RerollProtectionOptions { Enabled = true, Exclusion = RerollExclusionMode.None };
            configure?.Invoke(options);
            return options;
        }

        private static RerollProtectionOptions PreviousWinner() => Options(o => o.Exclusion = RerollExclusionMode.PreviousWinner);

        private SelectionResult Select(RerollProtectionOptions options) => protection.BuildSelection(wheel.Id, options);

        private void Win(int index, RerollProtectionOptions options) => protection.RecordSpin(wheel.Id, games[index].Id, options);

        private static List<Guid> Eligible(SelectionResult result) => result.EligibleIndices.Select(i => result.Entries[i].Id).ToList();

        [Fact]
        public void Off_EveryGameIsEligible_AndNoStateIsKept()
        {
            var off = new RerollProtectionOptions();
            var saves = store.SaveCount;

            Win(0, off);
            var selection = Select(off);

            Assert.False(selection.ProtectionEnabled);
            Assert.Equal(5, selection.EligibleCount);
            Assert.Null(selection.RerollsRemaining);
            Assert.Null(wheels.GetWheel(wheel.Id).Reroll);
            Assert.Equal(saves, store.SaveCount);
        }

        [Fact]
        public void PreviousWinner_IsExcludedFromTheNextSpinOnly()
        {
            var options = PreviousWinner();

            Win(2, options);
            var first = Select(options);
            Assert.Equal(4, first.EligibleCount);
            Assert.DoesNotContain(games[2].Id, Eligible(first));
            Assert.Equal(new[] { games[2].Id }, first.ExcludedIds);

            Win(4, options);
            var second = Select(options);
            Assert.Contains(games[2].Id, Eligible(second));
            Assert.DoesNotContain(games[4].Id, Eligible(second));
        }

        [Fact]
        public void RecentWinners_ExcludesTheConfiguredNumber()
        {
            var options = Options(o => { o.Exclusion = RerollExclusionMode.RecentWinners; o.RecentWinnerCount = 3; });

            foreach (var index in new[] { 0, 1, 2, 3 })
            {
                Win(index, options);
            }

            Assert.Equal(new[] { games[0].Id, games[4].Id }, Eligible(Select(options)));
        }

        [Fact]
        public void ExcludedGames_HaveZeroProbability_AndTheRestStayEqual()
        {
            var options = PreviousWinner();
            Win(1, options);
            var selection = Select(options);
            var randomiser = new RandomiserService(new SeededRandomSource(99));
            var wins = new int[5];

            for (var i = 0; i < 40_000; i++)
            {
                wins[randomiser.PickWinnerIndex(selection.EligibleIndices)]++;
            }

            Assert.Equal(0, wins[1]);
            foreach (var index in new[] { 0, 2, 3, 4 })
            {
                Assert.InRange(wins[index], 9_500, 10_500);
            }
        }

        [Fact]
        public void PlannedSpin_OnlyEverLandsOnAnEligibleSegment_OfTheWholeWheel()
        {
            var options = PreviousWinner();
            Win(3, options);
            var selection = Select(options);
            var randomiser = new RandomiserService(new SeededRandomSource(5));

            for (var i = 0; i < 500; i++)
            {
                var plan = randomiser.PlanSpin(selection.Entries.Count, selection.EligibleIndices, 0, new SpinOptions());
                Assert.Contains(plan.WinnerIndex, selection.EligibleIndices);
                Assert.NotEqual(3, plan.WinnerIndex);
                Assert.Equal(5, plan.SegmentCount);
            }
        }

        [Fact]
        public void RerollLimit_BlocksAfterTheAllowedRerolls_UntilThePickIsAccepted()
        {
            var options = Options(o => { o.LimitRerolls = true; o.MaxRerolls = 2; });
            Assert.Equal(2, Select(options).RerollsRemaining);
            Assert.False(Select(options).SessionActive);

            Win(0, options);
            Assert.Equal(2, Select(options).RerollsRemaining);
            Assert.True(Select(options).SessionActive);
            Win(1, options);
            Assert.Equal(1, Select(options).RerollsRemaining);
            Win(2, options);

            var blocked = Select(options);
            Assert.Equal(SpinBlockReason.RerollLimitReached, blocked.BlockReason);
            Assert.Equal(UserMessages.RerollLimitReached, blocked.Message);
            Assert.Equal(0, blocked.EligibleCount);
            Assert.Equal(0, blocked.RerollsRemaining);

            Assert.True(protection.Accept(wheel.Id));
            var fresh = Select(options);
            Assert.False(fresh.IsBlocked);
            Assert.Equal(2, fresh.RerollsRemaining);
            Assert.False(fresh.SessionActive);
            Assert.False(protection.Accept(wheel.Id));
        }

        [Fact]
        public void Cooldown_BlocksUntilItExpires_ThenRestoresRerolls()
        {
            var options = Options(o => { o.LimitRerolls = true; o.MaxRerolls = 1; o.CooldownEnabled = true; o.CooldownMinutes = 10; });
            var started = clock.UtcNow;
            Win(0, options);
            Win(1, options);

            var blocked = Select(options);
            Assert.Equal(SpinBlockReason.CoolingDown, blocked.BlockReason);
            Assert.Equal(started.AddMinutes(10), blocked.CooldownUntilUtc);
            Assert.Contains("10 minutes", blocked.Message);

            clock.UtcNow = started.AddMinutes(9);
            Assert.Contains("1 minute,", Select(options).Message);

            clock.UtcNow = started.AddMinutes(10);
            var after = Select(options);
            Assert.False(after.IsBlocked);
            Assert.Equal(1, after.RerollsRemaining);
            Assert.False(after.SessionActive);
        }

        [Fact]
        public void ManualReset_ClearsRecentWinnersCountAndCooldown()
        {
            var options = Options(o => { o.Exclusion = RerollExclusionMode.PreviousWinner; o.LimitRerolls = true; o.MaxRerolls = 1; });
            Win(0, options);
            Win(1, options);
            Assert.True(Select(options).IsBlocked);

            Assert.True(protection.Reset(wheel.Id));

            var selection = Select(options);
            Assert.False(selection.IsBlocked);
            Assert.Equal(5, selection.EligibleCount);
            Assert.Null(wheels.GetWheel(wheel.Id).Reroll);
            Assert.False(protection.Reset(wheel.Id));
        }

        [Fact]
        public void SingleRemainingCandidate_CanStillBePicked()
        {
            var options = Options(o => { o.Exclusion = RerollExclusionMode.RecentWinners; o.RecentWinnerCount = 4; });
            foreach (var index in new[] { 0, 1, 2, 3 })
            {
                Win(index, options);
            }

            var selection = Select(options);

            Assert.False(selection.IsBlocked);
            Assert.Equal(new[] { games[4].Id }, Eligible(selection));
            Assert.False(selection.ExclusionBypassed);
        }

        [Fact]
        public void SingleGameWheel_IsNeverMadeUnusable()
        {
            var solo = wheels.CreateWheel("Solo", new[] { games[0].Id });
            var options = PreviousWinner();
            protection.RecordSpin(solo.Id, games[0].Id, options);

            var selection = protection.BuildSelection(solo.Id, options);

            Assert.False(selection.IsBlocked);
            Assert.True(selection.ExclusionBypassed);
            Assert.Equal(UserMessages.SingleGameBypass, selection.Message);
            Assert.Equal(1, selection.EligibleCount);
        }

        [Fact]
        public void AllCandidatesExcluded_ExplainsWhy_AndNeverPicksAnExcludedGame()
        {
            var duo = wheels.CreateWheel("Duo", new[] { games[0].Id, games[1].Id });
            var options = Options(o => { o.Exclusion = RerollExclusionMode.RecentWinners; o.RecentWinnerCount = 2; });
            protection.RecordSpin(duo.Id, games[0].Id, options);
            protection.RecordSpin(duo.Id, games[1].Id, options);

            var blocked = protection.BuildSelection(duo.Id, options);

            Assert.Equal(SpinBlockReason.AllCandidatesExcluded, blocked.BlockReason);
            Assert.Equal(UserMessages.AllCandidatesExcluded, blocked.Message);
            Assert.Empty(blocked.EligibleIndices);
            Assert.Throws<InvalidOperationException>(() => new RandomiserService(new SeededRandomSource(1)).PickWinnerIndex(blocked.EligibleIndices));

            protection.Reset(duo.Id);
            Assert.Equal(2, protection.BuildSelection(duo.Id, options).EligibleCount);
        }

        [Fact]
        public void EmptyWheel_IsBlockedWithAReason()
        {
            var empty = wheels.CreateWheel("Empty");

            Assert.Equal(SpinBlockReason.EmptyWheel, protection.BuildSelection(empty.Id, PreviousWinner()).BlockReason);
            Assert.Equal(SpinBlockReason.EmptyWheel, protection.BuildSelection(empty.Id, new RerollProtectionOptions()).BlockReason);
        }

        [Fact]
        public void DuplicateEntries_GetOnlyOneChance()
        {
            var entries = new[] { games[0], games[1], games[0] };

            var selection = RerollProtectionService.Evaluate(entries, null, new RerollProtectionOptions(), clock.UtcNow);

            Assert.Equal(new[] { 0, 1 }, selection.EligibleIndices);
        }

        [Fact]
        public void HiddenAndDeletedGames_AreNeverCandidates()
        {
            games[0].IsHidden = true;
            catalog.Remove(games[1].Id);

            var selection = Select(PreviousWinner());

            Assert.Equal(3, selection.Entries.Count);
            Assert.DoesNotContain(games[0].Id, Eligible(selection));
            Assert.DoesNotContain(games[1].Id, Eligible(selection));
        }

        [Fact]
        public void InvalidPersistedState_IsHandledSafely_WithoutBeingModified()
        {
            var options = Options(o =>
            {
                o.Exclusion = RerollExclusionMode.RecentWinners;
                o.RecentWinnerCount = 2;
                o.LimitRerolls = true;
                o.MaxRerolls = 1;
                o.CooldownEnabled = true;
                o.CooldownMinutes = 10;
            });
            var state = new RerollState
            {
                RecentWinnerIds = new List<Guid> { Guid.Empty, games[2].Id, games[2].Id, games[3].Id },
                RerollCount = -3,
                SessionId = Guid.NewGuid(),
                CooldownUntilUtc = clock.UtcNow.AddYears(5)
            };

            var selection = RerollProtectionService.Evaluate(games, state, options, clock.UtcNow);
            Assert.False(selection.IsBlocked);
            Assert.Equal(new[] { games[0].Id, games[1].Id, games[4].Id }, Eligible(selection));
            Assert.Equal(-3, state.RerollCount);
            Assert.Equal(4, state.RecentWinnerIds.Count);

            // An absurd count and a cooldown years away are clamped to what the settings allow.
            state.RerollCount = 99;
            var blocked = RerollProtectionService.Evaluate(games, state, options, clock.UtcNow);
            Assert.Equal(SpinBlockReason.CoolingDown, blocked.BlockReason);
            Assert.Equal(clock.UtcNow.AddMinutes(10), blocked.CooldownUntilUtc);

            Assert.Equal(5, RerollProtectionService.Evaluate(games, null, options, clock.UtcNow).EligibleCount);
        }

        [Fact]
        public void ExpiredCooldown_InPersistedState_NoLongerBlocks()
        {
            var options = Options(o => { o.LimitRerolls = true; o.MaxRerolls = 1; o.CooldownEnabled = true; });
            var state = new RerollState { SessionId = Guid.NewGuid(), RerollCount = 5, CooldownUntilUtc = clock.UtcNow.AddHours(-1) };

            var selection = RerollProtectionService.Evaluate(games, state, options, clock.UtcNow);

            Assert.False(selection.IsBlocked);
            Assert.False(selection.SessionActive);
            Assert.Equal(1, selection.RerollsRemaining);
        }

        [Fact]
        public void ProtectionState_SurvivesASaveAndLoad()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var options = Options(o => { o.Exclusion = RerollExclusionMode.PreviousWinner; o.LimitRerolls = true; o.MaxRerolls = 1; o.CooldownEnabled = true; });
                var first = Build.Service(catalog, Build.FileStore(path), clock: clock);
                var saved = first.CreateWheel("Persisted", games.Select(g => g.Id));
                var service = new RerollProtectionService(first);
                service.RecordSpin(saved.Id, games[0].Id, options);
                service.RecordSpin(saved.Id, games[1].Id, options);
                var expected = first.GetWheel(saved.Id).Reroll;

                var reloaded = Build.Service(catalog, Build.FileStore(path), clock: clock).GetWheel(saved.Id).Reroll;

                Assert.Equal(expected.RecentWinnerIds, reloaded.RecentWinnerIds);
                Assert.Equal(1, reloaded.RerollCount);
                Assert.Equal(expected.SessionId, reloaded.SessionId);
                Assert.Equal(expected.CooldownUntilUtc, reloaded.CooldownUntilUtc);
                Assert.Equal(DateTimeKind.Utc, reloaded.CooldownUntilUtc.Value.Kind);
            }
        }

        [Fact]
        public void ResetAll_ClearsEveryWheelInOneSave()
        {
            var other = wheels.CreateWheel("Other", games.Select(g => g.Id));
            var options = PreviousWinner();
            Win(0, options);
            protection.RecordSpin(other.Id, games[1].Id, options);
            var saves = store.SaveCount;

            Assert.Equal(2, protection.ResetAll());

            Assert.Equal(saves + 1, store.SaveCount);
            Assert.All(wheels.Wheels, w => Assert.Null(w.Reroll));
            Assert.Equal(0, protection.ResetAll());
        }

        [Fact]
        public void ASpin_RecordsHistoryAndProtectionInOneSave()
        {
            var history = new HistoryService(wheels);
            var saves = store.SaveCount;

            using (wheels.Batch())
            {
                history.Record(wheel.Id, games[0]);
                protection.RecordSpin(wheel.Id, games[0].Id, PreviousWinner());
            }

            Assert.Equal(saves + 1, store.SaveCount);
        }

        [Fact]
        public void DeletedGame_IsForgottenAsARecentWinner()
        {
            Win(0, PreviousWinner());

            wheels.ForgetGames(new[] { games[0].Id });

            Assert.Empty(wheels.GetWheel(wheel.Id).Reroll.RecentWinnerIds);
        }

        [Fact]
        public void Options_AreClampedToSafeRanges()
        {
            var options = new RerollProtectionOptions
            {
                RecentWinnerCount = 99,
                MaxRerolls = 0,
                CooldownMinutes = -5,
                Exclusion = (RerollExclusionMode)9,
                ResetBehaviour = (RerollResetBehaviour)9
            }.Sanitize();

            Assert.Equal(10, options.RecentWinnerCount);
            Assert.Equal(1, options.MaxRerolls);
            Assert.Equal(1, options.CooldownMinutes);
            Assert.Equal(RerollExclusionMode.PreviousWinner, options.Exclusion);
            Assert.Equal(RerollResetBehaviour.ResetOnRestart, options.ResetBehaviour);
        }
    }
}
