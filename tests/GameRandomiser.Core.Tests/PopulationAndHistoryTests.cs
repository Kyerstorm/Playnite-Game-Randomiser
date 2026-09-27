using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Population;
using GameRandomiser.Core.Services;
using Xunit;

namespace GameRandomiser.Core.Tests
{
    public class PopulationTests
    {
        private readonly FakeCatalog catalog = new FakeCatalog();
        private readonly FixedClock clock = new FixedClock();
        private readonly Guid rpg = Guid.NewGuid();
        private readonly Guid shooter = Guid.NewGuid();
        private readonly Guid pc = Guid.NewGuid();
        private readonly Guid completed = Guid.NewGuid();
        private readonly Guid tagCoop = Guid.NewGuid();
        private readonly Guid catFav = Guid.NewGuid();
        private readonly GameInfo neverPlayed, lightlyPlayed, heavilyPlayed, hiddenRpg, recent;

        public PopulationTests()
        {
            neverPlayed = catalog.Add("Never Played RPG", g => { g.GenreIds = new[] { rpg }; g.PlatformIds = new[] { pc }; });
            lightlyPlayed = catalog.Add("Light Shooter", g => { g.GenreIds = new[] { shooter }; g.PlaytimeSeconds = 3600; g.LastActivity = clock.UtcNow.AddDays(-100); g.IsInstalled = true; g.TagIds = new[] { tagCoop }; });
            heavilyPlayed = catalog.Add("Heavy RPG", g => { g.GenreIds = new[] { rpg, shooter }; g.PlaytimeSeconds = 50 * 3600; g.CompletionStatusId = completed; g.LastActivity = clock.UtcNow.AddDays(-400); g.CategoryIds = new[] { catFav }; });
            hiddenRpg = catalog.Add("Hidden RPG", g => { g.GenreIds = new[] { rpg }; g.IsHidden = true; });
            recent = catalog.Add("Recent", g => { g.PlaytimeSeconds = 600; g.LastActivity = clock.UtcNow.AddDays(-3); });
        }

        private IReadOnlyList<GameInfo> Run(PopulationSource source, double? amount = null, params Guid[] items) =>
            new PopulationEngine(catalog, clock: clock).Evaluate(new PopulationSpec { Source = source, Amount = amount, ItemIds = items.ToList() });

        [Fact]
        public void AllGames_ExcludesHidden()
        {
            var result = Run(PopulationSource.AllGames);
            Assert.Equal(4, result.Count);
            Assert.DoesNotContain(hiddenRpg, result);
        }

        [Fact]
        public void Genre_MatchesAny_ExcludesHidden()
        {
            var result = Run(PopulationSource.Genre, null, rpg);
            Assert.Equal(new[] { heavilyPlayed.Id, neverPlayed.Id }, result.Select(g => g.Id));
        }

        [Fact]
        public void Lookup_WithNoSelection_ReturnsNothing()
        {
            Assert.Empty(Run(PopulationSource.Genre));
        }

        [Fact]
        public void Platform_Tag_Category_Status()
        {
            Assert.Equal(new[] { neverPlayed }, Run(PopulationSource.Platform, null, pc));
            Assert.Equal(new[] { lightlyPlayed }, Run(PopulationSource.Tag, null, tagCoop));
            Assert.Equal(new[] { heavilyPlayed }, Run(PopulationSource.Category, null, catFav));
            Assert.Equal(new[] { heavilyPlayed }, Run(PopulationSource.CompletionStatus, null, completed));
        }

        [Fact]
        public void NeverPlayed_And_Played()
        {
            Assert.Equal(new[] { neverPlayed }, Run(PopulationSource.NeverPlayed));
            Assert.Equal(3, Run(PopulationSource.Played).Count);
        }

        [Fact]
        public void Playtime_Thresholds()
        {
            var under2h = Run(PopulationSource.PlaytimeLessThan, 2).Select(g => g.Name).ToList();
            Assert.Equal(new[] { "Light Shooter", "Never Played RPG", "Recent" }, under2h);
            Assert.Equal(new[] { heavilyPlayed }, Run(PopulationSource.PlaytimeMoreThan, 10));
        }

        [Fact]
        public void RecentlyPlayed_WithinDays()
        {
            Assert.Equal(new[] { recent }, Run(PopulationSource.RecentlyPlayed, 30));
            Assert.Equal(2, Run(PopulationSource.RecentlyPlayed, 200).Count);
        }

        [Fact]
        public void Installed_NotInstalled()
        {
            Assert.Equal(new[] { lightlyPlayed }, Run(PopulationSource.Installed));
            Assert.Equal(3, Run(PopulationSource.NotInstalled).Count);
        }

        [Fact]
        public void FilteredLibrary_UsesCatalogFilter_AndStillExcludesHidden()
        {
            catalog.Filtered.AddRange(new[] { recent, hiddenRpg });
            Assert.Equal(new[] { recent }, Run(PopulationSource.FilteredLibrary));
        }

        [Fact]
        public void Registry_IsExtensible()
        {
            var registry = PopulationRuleRegistry.CreateDefault();
            registry.Register(new PredicateRule(PopulationSource.AllGames, "Starts with H", "", (g, s, n) => g.Name.StartsWith("H")));
            var result = new PopulationEngine(catalog, registry, clock).Evaluate(new PopulationSpec { Source = PopulationSource.AllGames });
            Assert.Equal(new[] { heavilyPlayed }, result);
        }

        [Fact]
        public void EveryPopulationSource_HasARule()
        {
            var registry = PopulationRuleRegistry.CreateDefault();
            foreach (PopulationSource source in Enum.GetValues(typeof(PopulationSource)))
            {
                Assert.NotNull(registry.Get(source));
            }
        }

        [Fact]
        public void Describe_UsesLookupNames()
        {
            catalog.Lookups[LookupKind.Genre] = new List<NamedItem> { new NamedItem(rpg, "RPG") };
            var engine = new PopulationEngine(catalog, clock: clock);
            Assert.Equal("Genre: RPG", engine.Describe(new PopulationSpec { Source = PopulationSource.Genre, ItemIds = { rpg } }));
            Assert.Equal("Playtime under 2 h", engine.Describe(new PopulationSpec { Source = PopulationSource.PlaytimeLessThan, Amount = 2 }));
        }
    }

    public class HistoryTests
    {
        [Fact]
        public void Record_IsPerWheel_MostRecentFirst()
        {
            var catalog = new FakeCatalog();
            var clock = new FixedClock();
            var hades = catalog.Add("Hades");
            var celeste = catalog.Add("Celeste");
            var service = Build.Service(catalog, clock: clock);
            var history = new HistoryService(service);
            var a = service.CreateWheel("A", new[] { hades.Id, celeste.Id });
            var b = service.CreateWheel("B", new[] { hades.Id });

            history.Record(a.Id, hades);
            clock.UtcNow = clock.UtcNow.AddMinutes(5);
            history.Record(a.Id, celeste);
            history.Record(b.Id, hades);

            var aHistory = history.GetHistory(a.Id);
            Assert.Equal(new[] { "Celeste", "Hades" }, aHistory.Select(h => h.GameName));
            Assert.True(aHistory[0].SpunAtUtc > aHistory[1].SpunAtUtc);
            Assert.Single(history.GetHistory(b.Id));
        }

        [Fact]
        public void History_IsCapped()
        {
            var catalog = new FakeCatalog();
            var game = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var history = new HistoryService(service);
            var wheel = service.CreateWheel("A", new[] { game.Id });

            for (var i = 0; i < HistoryService.MaxEntriesPerWheel + 25; i++)
            {
                history.Record(wheel.Id, game);
            }

            Assert.Equal(HistoryService.MaxEntriesPerWheel, history.GetHistory(wheel.Id).Count);
        }

        [Fact]
        public void Clear_And_RemoveEntriesFor()
        {
            var catalog = new FakeCatalog();
            var hades = catalog.Add("Hades");
            var celeste = catalog.Add("Celeste");
            var service = Build.Service(catalog);
            var history = new HistoryService(service);
            var wheel = service.CreateWheel("A", new[] { hades.Id, celeste.Id });
            history.Record(wheel.Id, hades);
            history.Record(wheel.Id, celeste);

            Assert.Equal(1, history.RemoveEntriesFor(new[] { hades.Id }));
            Assert.Single(history.GetHistory(wheel.Id));

            history.Clear(wheel.Id);
            Assert.Empty(history.GetHistory(wheel.Id));
        }

        [Fact]
        public void History_SurvivesGameRemovalFromWheel()
        {
            var catalog = new FakeCatalog();
            var hades = catalog.Add("Hades");
            var service = Build.Service(catalog);
            var history = new HistoryService(service);
            var wheel = service.CreateWheel("A", new[] { hades.Id });
            history.Record(wheel.Id, hades);

            service.RemoveGames(wheel.Id, new[] { hades.Id });

            Assert.Single(history.GetHistory(wheel.Id));
        }
    }
}
