using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Services;
using Xunit;

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
}
