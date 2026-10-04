using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Population;
using GameRandomiser.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace GameRandomiser.Core.Tests
{
    public class WheelServiceTests
    {
        private readonly FakeCatalog catalog = new FakeCatalog();

        [Fact]
        public void CreateWheel_AddsAndActivates()
        {
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog");

            Assert.Single(service.Wheels);
            Assert.Equal("Backlog", wheel.Name);
            Assert.Equal(wheel.Id, service.ActiveWheel.Id);
        }

        [Fact]
        public void CreateWheel_RaisesChangedAndSaves()
        {
            var store = new InMemoryStore();
            var service = Build.Service(catalog, store);
            var events = new List<WheelChangeKind>();
            service.Changed += (s, e) => events.Add(e.Kind);

            service.CreateWheel("RPGs");

            Assert.Contains(WheelChangeKind.WheelAdded, events);
            Assert.Equal(1, store.SaveCount);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void CreateWheel_RejectsBlankName(string name)
        {
            var service = Build.Service(catalog);
            Assert.Throws<ArgumentException>(() => service.CreateWheel(name));
        }

        [Fact]
        public void CreateWheel_RejectsDuplicateNameCaseInsensitive()
        {
            var service = Build.Service(catalog);
            service.CreateWheel("Backlog");
            var ex = Assert.Throws<ArgumentException>(() => service.CreateWheel("backlog"));
            Assert.Contains("already exists", ex.Message);
        }

        [Fact]
        public void RenameWheel_ChangesName_AndAllowsSameNameOnSelf()
        {
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog");
            service.RenameWheel(wheel.Id, "  Pile of Shame  ");
            Assert.Equal("Pile of Shame", service.GetWheel(wheel.Id).Name);

            service.RenameWheel(wheel.Id, "PILE OF SHAME");
            Assert.Equal("PILE OF SHAME", service.GetWheel(wheel.Id).Name);
        }

        [Fact]
        public void DeleteWheel_SwitchesActiveToNeighbour()
        {
            var service = Build.Service(catalog);
            var a = service.CreateWheel("A");
            var b = service.CreateWheel("B");
            var c = service.CreateWheel("C");
            service.SetActiveWheel(b.Id);

            Assert.True(service.DeleteWheel(b.Id));

            Assert.Equal(2, service.Wheels.Count);
            Assert.Equal(c.Id, service.ActiveWheel.Id);
            Assert.False(service.DeleteWheel(b.Id));
        }

        [Fact]
        public void DeleteLastWheel_LeavesNoActiveWheel()
        {
            var service = Build.Service(catalog);
            var a = service.CreateWheel("A");
            service.DeleteWheel(a.Id);
            Assert.Null(service.ActiveWheel);
        }

        [Fact]
        public void SetActiveWheel_Switches()
        {
            var service = Build.Service(catalog);
            var a = service.CreateWheel("A");
            service.CreateWheel("B");
            service.SetActiveWheel(a.Id);
            Assert.Equal(a.Id, service.ActiveWheel.Id);
        }

        [Fact]
        public void SetActiveWheel_UnknownIdThrows()
        {
            var service = Build.Service(catalog);
            Assert.Throws<KeyNotFoundException>(() => service.SetActiveWheel(Guid.NewGuid()));
        }

        [Fact]
        public void AddOneGame()
        {
            var hades = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog");

            var result = service.AddGames(wheel.Id, new[] { hades.Id });

            Assert.Equal(1, result.Added);
            Assert.True(service.Contains(wheel.Id, hades.Id));
        }

        [Fact]
        public void AddMultipleGames_SortedAlphabetically()
        {
            var z = catalog.Add("Zelda");
            var a = catalog.Add("Alan Wake");
            var m = catalog.Add("Metroid", g => g.SortingName = "Metroid Prime");
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog");

            var result = service.AddGames(wheel.Id, new[] { z.Id, a.Id, m.Id });

            Assert.Equal(3, result.Added);
            Assert.Equal(new[] { a.Id, m.Id, z.Id }, service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void AddGames_PreventsDuplicates()
        {
            var hades = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog", new[] { hades.Id });

            var result = service.AddGames(wheel.Id, new[] { hades.Id, hades.Id });

            Assert.Equal(0, result.Added);
            Assert.Equal(2, result.AlreadyPresent);
            Assert.Single(service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void SameGame_CanBeInMultipleWheels()
        {
            var hades = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var a = service.CreateWheel("Backlog", new[] { hades.Id });
            var b = service.CreateWheel("Roguelikes", new[] { hades.Id });

            Assert.Equal(2, service.WheelsContaining(hades.Id).Count);
            Assert.True(service.Contains(a.Id, hades.Id));
            Assert.True(service.Contains(b.Id, hades.Id));
        }

        [Fact]
        public void AddGames_RejectsHiddenAndMissing()
        {
            var hidden = catalog.Add("Secret", g => g.IsHidden = true);
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog");

            var result = service.AddGames(wheel.Id, new[] { hidden.Id, Guid.NewGuid() });

            Assert.Equal(0, result.Added);
            Assert.Equal(2, result.Ineligible);
            Assert.Empty(service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void AddGames_AcceptsUninstalled()
        {
            var bg3 = catalog.Add("Baldur's Gate 3", g => g.IsInstalled = false);
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("Backlog", new[] { bg3.Id });

            Assert.Single(service.ResolveEntries(wheel.Id));
            Assert.False(service.ResolveEntries(wheel.Id)[0].IsInstalled);
        }

        [Fact]
        public void RemoveGames_RemovesOnlyFromThatWheel()
        {
            var hades = catalog.Add("Hades");
            var celeste = catalog.Add("Celeste");
            var service = Build.Service(catalog);
            var a = service.CreateWheel("A", new[] { hades.Id, celeste.Id });
            var b = service.CreateWheel("B", new[] { hades.Id });

            Assert.Equal(1, service.RemoveGames(a.Id, new[] { hades.Id }));

            Assert.Equal(new[] { celeste.Id }, service.GetWheel(a.Id).GameIds);
            Assert.True(service.Contains(b.Id, hades.Id));
            Assert.NotNull(catalog.TryGet(hades.Id)); // never touches the library
        }

        [Fact]
        public void ClearGames_EmptiesWheel()
        {
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", new[] { catalog.Add("X").Id, catalog.Add("Y").Id });
            Assert.Equal(2, service.ClearGames(wheel.Id));
            Assert.Empty(service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void DeletedGame_IsSkippedOnResolve_AndCleanedUp()
        {
            var keep = catalog.Add("Keep");
            var gone = catalog.Add("Gone");
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", new[] { keep.Id, gone.Id });

            catalog.Remove(gone.Id);

            Assert.Single(service.ResolveEntries(wheel.Id));
            Assert.Equal(1, service.CleanupInvalidEntries());
            Assert.Equal(new[] { keep.Id }, service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void GameHiddenAfterAdding_IsNotEligible_AndCleanedUp()
        {
            var game = catalog.Add("Later Hidden");
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", new[] { game.Id });

            game.IsHidden = true;

            Assert.Empty(service.ResolveEntries(wheel.Id));
            service.CleanupInvalidEntries();
            Assert.Empty(service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void RemoveGameFromAllWheels()
        {
            var game = catalog.Add("Hades");
            var service = Build.Service(catalog);
            service.CreateWheel("A", new[] { game.Id });
            service.CreateWheel("B", new[] { game.Id });

            Assert.Equal(2, service.RemoveGameFromAllWheels(game.Id));
            Assert.Empty(service.WheelsContaining(game.Id));
        }

        [Fact]
        public void DuplicateWheel_CopiesGamesNotHistory_AndMakesUniqueName()
        {
            var game = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var history = new HistoryService(service);
            var wheel = service.CreateWheel("Backlog", new[] { game.Id });
            history.Record(wheel.Id, game);

            var copy = service.DuplicateWheel(wheel.Id);
            var copy2 = service.DuplicateWheel(wheel.Id);

            Assert.Equal("Backlog (copy)", copy.Name);
            Assert.Equal("Backlog (copy) 2", copy2.Name);
            Assert.Equal(new[] { game.Id }, copy.GameIds);
            Assert.Empty(copy.History);
            Assert.NotSame(wheel.GameIds, copy.GameIds);
        }

        [Fact]
        public void SortMode_LibraryOrder()
        {
            var first = catalog.Add("Zeta");   // LibraryIndex 0
            var second = catalog.Add("Alpha"); // LibraryIndex 1
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", new[] { first.Id, second.Id });
            Assert.Equal(new[] { second.Id, first.Id }, service.GetWheel(wheel.Id).GameIds);

            service.SetSortMode(wheel.Id, SortMode.Library);

            Assert.Equal(new[] { first.Id, second.Id }, service.GetWheel(wheel.Id).GameIds);
        }

        [Fact]
        public void Shuffle_ChangesArrangement_NotMembership_NotHistory()
        {
            var ids = Enumerable.Range(0, 30).Select(i => catalog.Add("Game " + i.ToString("00")).Id).ToList();
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", ids);
            var before = service.GetWheel(wheel.Id).GameIds.ToList();

            service.Shuffle(wheel.Id);

            var after = service.GetWheel(wheel.Id).GameIds;
            Assert.NotEqual(before, after);
            Assert.Equal(before.OrderBy(x => x), after.OrderBy(x => x));
            Assert.Equal(SortMode.Random, service.GetWheel(wheel.Id).SortMode);
            Assert.Empty(service.GetWheel(wheel.Id).History);
        }

        [Fact]
        public void RandomMode_InsertsNewGamesWithoutDuplicates()
        {
            var service = Build.Service(catalog);
            var wheel = service.CreateWheel("A", Enumerable.Range(0, 5).Select(i => catalog.Add("G" + i).Id), SortMode.Random);
            var extra = catalog.Add("Extra");

            service.AddGames(wheel.Id, new[] { extra.Id });

            Assert.Equal(6, service.GetWheel(wheel.Id).GameIds.Distinct().Count());
        }

        [Fact]
        public void Wheels_PersistAcrossRestart()
        {
            using (var dir = new TempDirectory())
            {
                var path = dir.File("data.json");
                var hades = catalog.Add("Hades");
                var celeste = catalog.Add("Celeste");

                var first = Build.Service(catalog, Build.FileStore(path));
                var backlog = first.CreateWheel("Backlog", new[] { hades.Id, celeste.Id });
                var rpg = first.CreateWheel("RPGs", new[] { hades.Id });
                first.SetSortMode(backlog.Id, SortMode.Library);
                first.SetActiveWheel(rpg.Id);
                new HistoryService(first).Record(rpg.Id, hades);

                // Simulate Playnite restart: brand new service reading the same file.
                var second = Build.Service(catalog, Build.FileStore(path));

                Assert.Equal(new[] { "Backlog", "RPGs" }, second.Wheels.Select(w => w.Name));
                Assert.Equal(rpg.Id, second.ActiveWheel.Id);
                Assert.Equal(SortMode.Library, second.GetWheel(backlog.Id).SortMode);
                Assert.Equal(first.GetWheel(backlog.Id).GameIds, second.GetWheel(backlog.Id).GameIds);
                var history = new HistoryService(second).GetHistory(rpg.Id);
                Assert.Single(history);
                Assert.Equal(hades.Id, history[0].GameId);
            }
        }
    }

    /// <summary>Dynamic membership: the reconciliation rules and the refresh coordinator.</summary>
    public class DynamicWheelTests
    {
        private readonly ITestOutputHelper output;
        private readonly FakeCatalog catalog = new FakeCatalog();
        private readonly InMemoryStore store = new InMemoryStore();
        private readonly ManualScheduler scheduler = new ManualScheduler();
        private readonly RecordingReporter errors = new RecordingReporter();
        private readonly FixedClock clock = new FixedClock();
        private readonly List<WheelRefreshResult> results = new List<WheelRefreshResult>();
        private readonly Guid rpg = Guid.NewGuid();
        private readonly WheelService wheels;
        private readonly PopulationEngine engine;
        private readonly RefreshCoordinator coordinator;
        private readonly GameInfo alpha, bravo, charlie, hidden;

        public DynamicWheelTests(ITestOutputHelper output)
        {
            this.output = output;
            alpha = catalog.Add("Alpha", g => { g.IsInstalled = true; g.GenreIds = new[] { rpg }; });
            bravo = catalog.Add("Bravo", g => g.IsInstalled = true);
            charlie = catalog.Add("Charlie");
            hidden = catalog.Add("Hidden", g => { g.IsInstalled = true; g.IsHidden = true; });
            wheels = Build.Service(catalog, store, clock: clock);
            engine = new PopulationEngine(catalog, clock: clock);
            coordinator = NewCoordinator(catalog, scheduler);
        }

        private RefreshCoordinator NewCoordinator(IGameCatalog source, ManualScheduler with)
        {
            var created = new RefreshCoordinator(wheels, engine, source, with, new SeededRandomSource(7), errors);
            created.Completed += (s, e) => results.AddRange(e.Results);
            return created;
        }

        private RandomiserWheel Create(string name, PopulationSpec spec, MembershipPolicy policy, SortMode sort = SortMode.Alphabetical) =>
            wheels.CreateWheel(name, engine.Evaluate(spec).Select(g => g.Id), sort, spec, policy: policy);

        private RandomiserWheel Installed(MembershipPolicy policy, string name = "Installed") =>
            Create(name, new PopulationSpec { Source = PopulationSource.Installed }, policy);

        private void LibraryChanged(LibraryFields fields)
        {
            coordinator.InvalidateLibrary(fields);
            scheduler.FireDebounce();
        }

        private IReadOnlyList<Guid> Ids(RandomiserWheel wheel) => wheels.GetWheel(wheel.Id).GameIds;

        // ---- Policies ----

        [Fact]
        public void NewDynamicWheel_ContainsOnlyVisibleMatchingGames()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);

            Assert.True(wheel.IsDynamic);
            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
            Assert.DoesNotContain(hidden.Id, Ids(wheel));
            Assert.Equal(RefreshStatus.Refreshed, wheels.GetRefreshInfo(wheel.Id).Status);
        }

        [Fact]
        public void Strict_AddsNewMatches_AndRemovesGamesThatStopMatching()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            charlie.IsInstalled = true;
            alpha.IsInstalled = false;

            LibraryChanged(LibraryFields.Installed);

            Assert.Equal(new[] { bravo.Id, charlie.Id }, Ids(wheel));
            var result = Assert.Single(results);
            Assert.Equal(RefreshResultKind.Updated, result.Kind);
            Assert.Equal(1, result.Added);
            Assert.Equal(1, result.Removed);
        }

        [Fact]
        public void UninstalledGame_StaysOnASnapshot_ButLeavesAStrictInstalledWheel()
        {
            var snapshot = Installed(MembershipPolicy.ManualSnapshot, "Snapshot");
            var strict = Installed(MembershipPolicy.StrictCriteria, "Strict");
            alpha.IsInstalled = false;

            LibraryChanged(LibraryFields.Installed);

            Assert.Contains(alpha.Id, Ids(snapshot));
            Assert.DoesNotContain(alpha.Id, Ids(strict));
        }

        [Fact]
        public void Pinned_GameStaysWhenItStopsMatching()
        {
            var wheel = Installed(MembershipPolicy.CriteriaPlusPinned);
            Assert.Equal(1, wheels.PinGames(wheel.Id, new[] { alpha.Id }));
            alpha.IsInstalled = false;
            bravo.IsInstalled = false;

            LibraryChanged(LibraryFields.Installed);

            Assert.Equal(new[] { alpha.Id }, Ids(wheel));
        }

        [Fact]
        public void Pinned_HiddenGameIsNeverEligible_AndDeletedGameIsForgotten()
        {
            var wheel = Installed(MembershipPolicy.CriteriaPlusPinned);
            wheels.PinGames(wheel.Id, new[] { alpha.Id });

            alpha.IsHidden = true;
            wheels.RemoveGamesFromAllWheels(new[] { alpha.Id });
            LibraryChanged(LibraryFields.Hidden);
            Assert.DoesNotContain(alpha.Id, Ids(wheel));
            Assert.True(wheels.IsPinned(wheel.Id, alpha.Id));

            alpha.IsHidden = false;
            LibraryChanged(LibraryFields.Hidden);
            Assert.Contains(alpha.Id, Ids(wheel));

            catalog.Remove(alpha.Id);
            wheels.ForgetGames(new[] { alpha.Id });
            LibraryChanged(LibraryFields.Collection);
            Assert.DoesNotContain(alpha.Id, Ids(wheel));
            Assert.False(wheels.IsPinned(wheel.Id, alpha.Id));
        }

        [Fact]
        public void ManualSnapshot_IsNeverChangedAutomatically()
        {
            var wheel = Installed(MembershipPolicy.ManualSnapshot);
            var saves = store.SaveCount;
            charlie.IsInstalled = true;
            alpha.IsInstalled = false;

            coordinator.InvalidateLibrary(LibraryFields.All);
            coordinator.InvalidateAll(RefreshReason.Startup);
            coordinator.InvalidateWheel(wheel.Id, RefreshReason.LibraryChanged);
            scheduler.FireDebounce();

            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
            Assert.Equal(0, coordinator.BatchesStarted);
            Assert.Equal(saves, store.SaveCount);
            Assert.Equal(RefreshStatus.NotApplicable, wheels.GetRefreshInfo(wheel.Id).Status);
        }

        [Fact]
        public void ManualSnapshot_ExplicitRefresh_AddsMatches_ButNeverRemoves()
        {
            var wheel = Installed(MembershipPolicy.ManualSnapshot);
            charlie.IsInstalled = true;
            alpha.IsInstalled = false;

            coordinator.InvalidateWheel(wheel.Id, RefreshReason.Manual);

            Assert.Equal(new[] { alpha.Id, bravo.Id, charlie.Id }, Ids(wheel));
            Assert.Equal(RefreshReason.Manual, Assert.Single(results).Reason);
        }

        [Fact]
        public void DynamicPolicy_RequiresCriteria_AndPinningRequiresThePinnedPolicy()
        {
            var plain = wheels.CreateWheel("Plain", new[] { alpha.Id });
            var strict = Installed(MembershipPolicy.StrictCriteria);

            var noCriteria = Assert.Throws<ArgumentException>(() => wheels.SetMembershipPolicy(plain.Id, MembershipPolicy.StrictCriteria));
            Assert.Equal(UserMessages.NoCriteria, noCriteria.Message);
            var noPins = Assert.Throws<ArgumentException>(() => wheels.PinGames(strict.Id, new[] { alpha.Id }));
            Assert.Equal(UserMessages.PinRequiresPolicy, noPins.Message);
        }

        // ---- Manual edits on dynamic wheels ----

        [Fact]
        public void RemovingAGame_FromADynamicWheel_Sticks_UntilItIsAddedBack()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);

            wheels.RemoveGames(wheel.Id, new[] { bravo.Id });
            LibraryChanged(LibraryFields.Collection);
            Assert.Equal(new[] { alpha.Id }, Ids(wheel));

            var added = wheels.AddGames(wheel.Id, new[] { bravo.Id });
            scheduler.FireDebounce();
            Assert.Equal(1, added.Added);
            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
            Assert.Empty(wheels.GetWheel(wheel.Id).ExcludedGameIds);
        }

        [Fact]
        public void StrictWheel_RejectsGamesThatDoNotMatch()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);

            var result = wheels.AddGames(wheel.Id, new[] { charlie.Id, alpha.Id, hidden.Id });

            Assert.Equal(0, result.Added);
            Assert.Equal(1, result.NotMatching);
            Assert.Equal(1, result.AlreadyPresent);
            Assert.Equal(1, result.Ineligible);
            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
        }

        [Fact]
        public void PinnedWheel_AddingAGame_PinsIt()
        {
            var wheel = Installed(MembershipPolicy.CriteriaPlusPinned);

            Assert.Equal(1, wheels.AddGames(wheel.Id, new[] { charlie.Id }).Added);
            scheduler.FireDebounce();

            Assert.True(wheels.IsPinned(wheel.Id, charlie.Id));
            Assert.Equal(new[] { alpha.Id, bravo.Id, charlie.Id }, Ids(wheel));
        }

        [Fact]
        public void SwitchingASnapshotToPinned_KeepsHandPickedGames()
        {
            var wheel = Installed(MembershipPolicy.ManualSnapshot);
            wheels.AddGames(wheel.Id, new[] { charlie.Id });

            wheels.SetMembershipPolicy(wheel.Id, MembershipPolicy.CriteriaPlusPinned);
            coordinator.InvalidateWheel(wheel.Id, RefreshReason.RulesChanged, pinUnmatched: true);
            scheduler.FireDebounce();

            Assert.Equal(new[] { alpha.Id, bravo.Id, charlie.Id }, Ids(wheel));
            Assert.True(wheels.IsPinned(wheel.Id, charlie.Id));
            Assert.False(wheels.IsPinned(wheel.Id, alpha.Id));
        }

        [Fact]
        public void SwitchingASnapshotToStrict_RemovesGamesThatDoNotMatch()
        {
            var wheel = Installed(MembershipPolicy.ManualSnapshot);
            wheels.AddGames(wheel.Id, new[] { charlie.Id });

            wheels.SetMembershipPolicy(wheel.Id, MembershipPolicy.StrictCriteria);
            scheduler.FireDebounce();

            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
        }

        [Fact]
        public void ChangingCriteria_RefreshesTheWheel()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);

            wheels.SetPopulation(wheel.Id, new PopulationSpec { Source = PopulationSource.NotInstalled });
            scheduler.FireDebounce();

            Assert.Equal(new[] { charlie.Id }, Ids(wheel));
        }

        // ---- Determinism ----

        [Fact]
        public void Refresh_IsIdempotent_AndWritesNothingWhenNothingChanged()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);
            var after = Ids(wheel).ToList();
            var saves = store.SaveCount;

            LibraryChanged(LibraryFields.Installed);
            LibraryChanged(LibraryFields.All);

            Assert.Equal(after, Ids(wheel));
            Assert.Equal(saves, store.SaveCount);
            Assert.Equal(RefreshResultKind.Unchanged, results.Last().Kind);
        }

        [Fact]
        public void RefreshedList_NeverContainsDuplicates()
        {
            var wheel = Installed(MembershipPolicy.CriteriaPlusPinned);
            wheels.PinGames(wheel.Id, new[] { alpha.Id, alpha.Id, bravo.Id });
            charlie.IsInstalled = true;

            LibraryChanged(LibraryFields.All);

            var ids = Ids(wheel);
            Assert.Equal(ids.Distinct().Count(), ids.Count);
            Assert.Equal(3, ids.Count);
        }

        [Fact]
        public void RandomlyArrangedWheel_KeepsExistingPositions_WhenAGameIsAdded()
        {
            var spec = new PopulationSpec { Source = PopulationSource.Installed };
            var wheel = Create("Shuffled", spec, MembershipPolicy.StrictCriteria, SortMode.Random);
            var before = Ids(wheel).ToList();
            charlie.IsInstalled = true;

            LibraryChanged(LibraryFields.Installed);

            Assert.Contains(charlie.Id, Ids(wheel));
            Assert.Equal(before, Ids(wheel).Where(id => id != charlie.Id));
        }

        // ---- Failures ----

        [Fact]
        public void FailedRefresh_KeepsTheLastKnownGoodList_AndSaysSo()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            var before = Ids(wheel).ToList();
            catalog.FailReads = true;

            LibraryChanged(LibraryFields.Installed);

            var info = wheels.GetRefreshInfo(wheel.Id);
            Assert.Equal(before, Ids(wheel));
            Assert.Equal(RefreshStatus.FailedUsingLastKnownGood, info.Status);
            Assert.True(info.IsStale);
            Assert.Equal(UserMessages.LibraryUnavailable, info.Error);
            var error = Assert.Single(errors.Errors);
            Assert.Equal(ErrorCategory.GameResolution, error.Category);
            Assert.Equal(wheel.Id, error.WheelId);

            // Failing again changes nothing worth writing.
            var saves = store.SaveCount;
            LibraryChanged(LibraryFields.Installed);
            Assert.Equal(saves, store.SaveCount);

            catalog.FailReads = false;
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);
            Assert.Equal(RefreshStatus.Refreshed, wheels.GetRefreshInfo(wheel.Id).Status);
            Assert.Null(wheels.GetRefreshInfo(wheel.Id).Error);
            Assert.Contains(charlie.Id, Ids(wheel));
        }

        [Fact]
        public void BackgroundFailure_IsObserved_AndKeepsTheList()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            var before = Ids(wheel).ToList();
            var source = new SnapshotCatalog(catalog) { Snapshot = () => throw new InvalidOperationException("worker died") };
            var other = new ManualScheduler();
            var failing = NewCoordinator(source, other);

            failing.InvalidateLibrary(LibraryFields.Installed);
            other.FireDebounce();

            Assert.Equal(before, Ids(wheel));
            Assert.Equal(RefreshResultKind.Failed, results.Single().Kind);
            Assert.Equal(UserMessages.RefreshFailed, wheels.GetRefreshInfo(wheel.Id).Error);
            Assert.False(failing.IsRunning);
        }

        [Fact]
        public void EmptyLibraryRead_NeverEmptiesAWheel()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            var source = new SnapshotCatalog(catalog) { Snapshot = () => new LibrarySnapshot(new GameInfo[0]) };
            var other = new ManualScheduler();
            var empty = NewCoordinator(source, other);

            empty.InvalidateLibrary(LibraryFields.Collection);
            other.FireDebounce();

            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));
            Assert.Equal(UserMessages.LibraryUnavailable, wheels.GetRefreshInfo(wheel.Id).Error);
        }

        [Fact]
        public void InvalidRule_FailsWithAnActionableMessage()
        {
            var wheel = wheels.CreateWheel("Broken", new[] { alpha.Id },
                population: new PopulationSpec { Source = (PopulationSource)999 }, policy: MembershipPolicy.StrictCriteria);

            coordinator.InvalidateWheel(wheel.Id, RefreshReason.Manual);

            var result = Assert.Single(results);
            Assert.Equal(RefreshResultKind.Failed, result.Kind);
            Assert.Equal(UserMessages.InvalidPopulationRule, result.ErrorMessage);
            Assert.Equal(ErrorCategory.PopulationRule, result.Category);
            Assert.Equal(new[] { alpha.Id }, Ids(wheel));
        }

        // ---- Coordination ----

        [Fact]
        public void BurstOfLibraryEvents_IsCoalescedIntoOneRefresh()
        {
            Installed(MembershipPolicy.StrictCriteria);

            for (var i = 0; i < 50; i++)
            {
                coordinator.InvalidateLibrary(LibraryFields.Installed);
            }

            Assert.Equal(50, scheduler.DebounceCalls);
            Assert.Equal(0, coordinator.BatchesStarted);

            Assert.True(scheduler.FireDebounce());
            Assert.Equal(1, coordinator.BatchesStarted);
            Assert.Single(results);
            Assert.False(scheduler.FireDebounce());
        }

        [Fact]
        public void OnlyWheelsThatDependOnTheChange_AreRefreshed()
        {
            var installed = Installed(MembershipPolicy.StrictCriteria);
            var genre = Create("RPGs", new PopulationSpec { Source = PopulationSource.Genre, ItemIds = { rpg } }, MembershipPolicy.StrictCriteria);

            LibraryChanged(LibraryFields.Genres);
            Assert.Equal(new[] { genre.Id }, results.Select(r => r.WheelId));

            results.Clear();
            LibraryChanged(LibraryFields.Playtime);
            Assert.Empty(results);

            // A game joining or leaving the library can affect any wheel.
            LibraryChanged(LibraryFields.Collection);
            Assert.Equal(new[] { installed.Id, genre.Id }.OrderBy(id => id), results.Select(r => r.WheelId).OrderBy(id => id));
        }

        [Fact]
        public void RefreshRequestedDuringASpin_WaitsForTheSpinToEnd()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            var spin = coordinator.Suspend();
            charlie.IsInstalled = true;

            LibraryChanged(LibraryFields.Installed);

            Assert.Equal(0, coordinator.BatchesStarted);
            Assert.True(coordinator.HasPending);
            Assert.Equal(new[] { alpha.Id, bravo.Id }, Ids(wheel));

            spin.Dispose();
            Assert.Equal(new[] { alpha.Id, bravo.Id, charlie.Id }, Ids(wheel));
        }

        [Fact]
        public void ResultArrivingDuringASpin_IsAppliedWhenTheSpinEnds()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            scheduler.HoldWork = true;
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);
            Assert.True(coordinator.IsRunning);
            Assert.Equal(RefreshStatus.Refreshing, wheels.GetRefreshInfo(wheel.Id).Status);

            var spin = coordinator.Suspend();
            scheduler.CompleteNext();
            Assert.DoesNotContain(charlie.Id, Ids(wheel));

            spin.Dispose();
            Assert.Contains(charlie.Id, Ids(wheel));
            Assert.Equal(RefreshStatus.Refreshed, wheels.GetRefreshInfo(wheel.Id).Status);
        }

        [Fact]
        public void EditDuringARefresh_DiscardsTheStaleResult_AndRefreshesAgain()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            scheduler.HoldWork = true;
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);

            wheels.RemoveGames(wheel.Id, new[] { bravo.Id });
            scheduler.CompleteNext();

            Assert.Equal(RefreshResultKind.Superseded, results.Last().Kind);
            Assert.Equal(new[] { alpha.Id }, Ids(wheel));

            Assert.True(scheduler.FireDebounce());
            scheduler.CompleteNext();
            Assert.Equal(new[] { alpha.Id, charlie.Id }, Ids(wheel));
        }

        [Fact]
        public void RequestsMadeWhileARefreshIsRunning_AreNotLost_AndNeverOverlap()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            scheduler.HoldWork = true;
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);

            alpha.IsInstalled = false;
            LibraryChanged(LibraryFields.Installed);
            Assert.Equal(1, coordinator.BatchesStarted);
            Assert.Equal(1, scheduler.PendingWork);

            scheduler.CompleteNext();
            Assert.Equal(2, coordinator.BatchesStarted);
            scheduler.CompleteNext();

            Assert.Equal(0, scheduler.PendingWork);
            Assert.False(coordinator.IsRunning);
            Assert.Equal(new[] { bravo.Id, charlie.Id }, Ids(wheel));
        }

        [Fact]
        public void Dispose_StopsEverything_AndIgnoresLateResults()
        {
            var wheel = Installed(MembershipPolicy.StrictCriteria);
            scheduler.HoldWork = true;
            charlie.IsInstalled = true;
            LibraryChanged(LibraryFields.Installed);

            coordinator.Dispose();
            scheduler.CompleteNext();
            var calls = scheduler.DebounceCalls;
            coordinator.InvalidateLibrary(LibraryFields.All);

            Assert.True(scheduler.Disposed);
            Assert.DoesNotContain(charlie.Id, Ids(wheel));
            Assert.Equal(calls, scheduler.DebounceCalls);
        }

        // ---- Scale ----

        [Theory]
        [InlineData(500)]
        [InlineData(2000)]
        [InlineData(5000)]
        [InlineData(10000)]
        public void LargeLibrary_RefreshesWithinBudget(int size)
        {
            var library = new FakeCatalog();
            var genre = Guid.NewGuid();
            for (var i = 0; i < size; i++)
            {
                var index = i;
                library.Add("Game " + index.ToString("00000"), g =>
                {
                    g.IsInstalled = index % 2 == 0;
                    g.PlaytimeSeconds = index % 3 == 0 ? 0UL : 3600UL;
                    g.GenreIds = index % 5 == 0 ? new[] { genre } : new Guid[0];
                });
            }

            var bigStore = new InMemoryStore();
            var service = Build.Service(library, bigStore, clock: clock);
            var with = new ManualScheduler();
            var refresh = new RefreshCoordinator(service, new PopulationEngine(library, clock: clock), library, with, new SeededRandomSource(3));
            var installed = service.CreateWheel("Installed", population: new PopulationSpec { Source = PopulationSource.Installed }, policy: MembershipPolicy.StrictCriteria);
            service.CreateWheel("Never played", population: new PopulationSpec { Source = PopulationSource.NeverPlayed }, policy: MembershipPolicy.StrictCriteria);
            service.CreateWheel("Genre", population: new PopulationSpec { Source = PopulationSource.Genre, ItemIds = { genre } }, policy: MembershipPolicy.CriteriaPlusPinned);
            service.CreateWheel("Everything", sortMode: SortMode.Library, population: new PopulationSpec { Source = PopulationSource.AllGames }, policy: MembershipPolicy.StrictCriteria);

            var populate = Stopwatch.StartNew();
            refresh.InvalidateAll(RefreshReason.Startup);
            with.FireDebounce();
            populate.Stop();

            var saves = bigStore.SaveCount;
            var unchanged = Stopwatch.StartNew();
            refresh.InvalidateLibrary(LibraryFields.All);
            with.FireDebounce();
            unchanged.Stop();

            output.WriteLine($"{size} games, 4 dynamic wheels: populate {populate.ElapsedMilliseconds} ms, no-change refresh {unchanged.ElapsedMilliseconds} ms");
            Assert.Equal(size / 2, service.GetWheel(installed.Id).GameIds.Count);
            Assert.Equal(1, saves - 4);
            Assert.Equal(saves, bigStore.SaveCount);
            Assert.True(populate.ElapsedMilliseconds < 5000, $"populate took {populate.ElapsedMilliseconds} ms");
            Assert.True(unchanged.ElapsedMilliseconds < 5000, $"refresh took {unchanged.ElapsedMilliseconds} ms");
        }
    }
}
