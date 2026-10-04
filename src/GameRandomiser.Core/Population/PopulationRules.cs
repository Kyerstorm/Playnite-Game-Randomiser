using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Population
{
    public enum RuleParameterKind
    {
        None,
        Lookup,
        Hours,
        Days
    }

    /// <summary>
    /// A way of choosing games from the library to populate a wheel. New rules can be added by
    /// implementing this interface and registering it with <see cref="PopulationRuleRegistry"/>.
    /// Rules only choose the pool; they never influence which game wins.
    /// </summary>
    public interface IPopulationRule
    {
        PopulationSource Source { get; }
        string DisplayName { get; }
        string HelpText { get; }
        RuleParameterKind ParameterKind { get; }

        /// <summary>For Lookup rules: which lookup list supplies choices.</summary>
        LookupKind? LookupKind { get; }

        double DefaultAmount { get; }

        /// <summary>
        /// Library aspects this rule reads beyond the collection itself and hidden state (which every
        /// rule depends on). Used to refresh only the dynamic wheels a library change can affect.
        /// </summary>
        LibraryFields Dependencies { get; }

        /// <summary>Candidate games (before hidden filtering) matching the spec.</summary>
        IEnumerable<GameInfo> Select(IGameCatalog catalog, PopulationSpec spec, DateTime utcNow);

        string Describe(PopulationSpec spec, IGameCatalog catalog);
    }

    /// <summary>Rule defined by a predicate over each game, optionally over a custom source list.</summary>
    public sealed class PredicateRule : IPopulationRule
    {
        private readonly Func<GameInfo, PopulationSpec, DateTime, bool> predicate;
        private readonly Func<IGameCatalog, IEnumerable<GameInfo>> source;
        private readonly Func<PopulationSpec, IGameCatalog, string> describe;

        public PredicateRule(
            PopulationSource sourceId,
            string displayName,
            string helpText,
            Func<GameInfo, PopulationSpec, DateTime, bool> predicate,
            RuleParameterKind parameterKind = RuleParameterKind.None,
            LookupKind? lookupKind = null,
            double defaultAmount = 0,
            Func<IGameCatalog, IEnumerable<GameInfo>> source = null,
            Func<PopulationSpec, IGameCatalog, string> describe = null,
            LibraryFields dependencies = LibraryFields.None)
        {
            Dependencies = dependencies;
            Source = sourceId;
            DisplayName = displayName;
            HelpText = helpText;
            ParameterKind = parameterKind;
            LookupKind = lookupKind;
            DefaultAmount = defaultAmount;
            this.predicate = predicate ?? ((g, s, n) => true);
            this.source = source ?? (c => c.GetAllGames());
            this.describe = describe;
        }

        public PopulationSource Source { get; }
        public string DisplayName { get; }
        public string HelpText { get; }
        public RuleParameterKind ParameterKind { get; }
        public LookupKind? LookupKind { get; }
        public double DefaultAmount { get; }
        public LibraryFields Dependencies { get; }

        public IEnumerable<GameInfo> Select(IGameCatalog catalog, PopulationSpec spec, DateTime utcNow) =>
            source(catalog).Where(g => predicate(g, spec, utcNow));

        public string Describe(PopulationSpec spec, IGameCatalog catalog)
        {
            if (describe != null)
            {
                return describe(spec, catalog);
            }

            if (ParameterKind == RuleParameterKind.Lookup && LookupKind.HasValue)
            {
                var lookup = catalog.GetLookup(LookupKind.Value).ToDictionary(i => i.Id, i => i.Name);
                var names = spec.ItemIds.Select(id => lookup.TryGetValue(id, out var n) ? n : null).Where(n => n != null).ToList();
                return names.Count == 0 ? DisplayName : $"{DisplayName}: {string.Join(", ", names)}";
            }

            return DisplayName;
        }
    }

    public sealed class PopulationRuleRegistry
    {
        private readonly List<IPopulationRule> rules = new List<IPopulationRule>();

        public static PopulationRuleRegistry CreateDefault()
        {
            var registry = new PopulationRuleRegistry();
            const double HourSeconds = 3600.0;

            registry.Register(new PredicateRule(PopulationSource.AllGames, "All games",
                "Every non-hidden game in your library.", (g, s, n) => true));

            registry.Register(new PredicateRule(PopulationSource.FilteredLibrary, "Current library filter",
                "Games currently shown in Playnite's library view with your active filters applied.",
                (g, s, n) => true, source: c => c.GetFilteredGames(), dependencies: LibraryFields.All));

            registry.Register(new PredicateRule(PopulationSource.NeverPlayed, "Never played",
                "Games with no recorded playtime or play sessions. Great for backlogs.",
                (g, s, n) => !g.HasBeenPlayed, dependencies: LibraryFields.Playtime | LibraryFields.Activity));

            registry.Register(new PredicateRule(PopulationSource.Played, "Played",
                "Games you have played at least once.", (g, s, n) => g.HasBeenPlayed,
                dependencies: LibraryFields.Playtime | LibraryFields.Activity));

            registry.Register(new PredicateRule(PopulationSource.CompletionStatus, "Play status",
                "Games with the chosen completion status (e.g. Playing, Completed, Abandoned).",
                (g, s, n) => s.ItemIds.Contains(g.CompletionStatusId),
                RuleParameterKind.Lookup, Models.LookupKind.CompletionStatus, dependencies: LibraryFields.CompletionStatus));

            registry.Register(new PredicateRule(PopulationSource.Genre, "Genre",
                "Games with any of the chosen genres.", (g, s, n) => g.GenreIds.Any(s.ItemIds.Contains),
                RuleParameterKind.Lookup, Models.LookupKind.Genre, dependencies: LibraryFields.Genres));

            registry.Register(new PredicateRule(PopulationSource.Platform, "Platform",
                "Games on any of the chosen platforms.", (g, s, n) => g.PlatformIds.Any(s.ItemIds.Contains),
                RuleParameterKind.Lookup, Models.LookupKind.Platform, dependencies: LibraryFields.Platforms));

            registry.Register(new PredicateRule(PopulationSource.Tag, "Tag",
                "Games with any of the chosen tags.", (g, s, n) => g.TagIds.Any(s.ItemIds.Contains),
                RuleParameterKind.Lookup, Models.LookupKind.Tag, dependencies: LibraryFields.Tags));

            registry.Register(new PredicateRule(PopulationSource.Category, "Category",
                "Games in any of the chosen categories.", (g, s, n) => g.CategoryIds.Any(s.ItemIds.Contains),
                RuleParameterKind.Lookup, Models.LookupKind.Category, dependencies: LibraryFields.Categories));

            registry.Register(new PredicateRule(PopulationSource.PlaytimeLessThan, "Playtime less than",
                "Games played for less than the given number of hours (includes never played).",
                (g, s, n) => g.PlaytimeSeconds < (s.Amount ?? 0) * HourSeconds,
                RuleParameterKind.Hours, defaultAmount: 2,
                describe: (s, c) => $"Playtime under {FormatAmount(s.Amount)} h", dependencies: LibraryFields.Playtime));

            registry.Register(new PredicateRule(PopulationSource.PlaytimeMoreThan, "Playtime more than",
                "Games played for more than the given number of hours.",
                (g, s, n) => g.PlaytimeSeconds > (s.Amount ?? 0) * HourSeconds,
                RuleParameterKind.Hours, defaultAmount: 10,
                describe: (s, c) => $"Playtime over {FormatAmount(s.Amount)} h", dependencies: LibraryFields.Playtime));

            registry.Register(new PredicateRule(PopulationSource.RecentlyPlayed, "Recently played",
                "Games last played within the given number of days.",
                (g, s, n) => g.LastActivity.HasValue && g.LastActivity.Value.ToUniversalTime() >= n.AddDays(-(s.Amount ?? 30)),
                RuleParameterKind.Days, defaultAmount: 30,
                describe: (s, c) => $"Played in the last {FormatAmount(s.Amount)} days", dependencies: LibraryFields.Activity));

            registry.Register(new PredicateRule(PopulationSource.Installed, "Installed",
                "Games that are currently installed.", (g, s, n) => g.IsInstalled, dependencies: LibraryFields.Installed));

            registry.Register(new PredicateRule(PopulationSource.NotInstalled, "Not installed",
                "Games that are not currently installed.", (g, s, n) => !g.IsInstalled, dependencies: LibraryFields.Installed));

            return registry;
        }

        public IReadOnlyList<IPopulationRule> Rules => rules.AsReadOnly();

        public void Register(IPopulationRule rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            rules.RemoveAll(r => r.Source == rule.Source);
            rules.Add(rule);
        }

        public IPopulationRule Get(PopulationSource source) => rules.FirstOrDefault(r => r.Source == source);

        private static string FormatAmount(double? amount) =>
            (amount ?? 0).ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Evaluates a spec to the final, eligible, de-duplicated game list.</summary>
    public sealed class PopulationEngine
    {
        private readonly IGameCatalog catalog;
        private readonly PopulationRuleRegistry registry;
        private readonly IClock clock;

        public PopulationEngine(IGameCatalog catalog, PopulationRuleRegistry registry = null, IClock clock = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.registry = registry ?? PopulationRuleRegistry.CreateDefault();
            this.clock = clock ?? new SystemClock();
        }

        public PopulationRuleRegistry Registry => registry;

        /// <summary>
        /// Evaluates against <paramref name="source"/> (a library snapshot) when given, otherwise the live catalog.
        /// Throws <see cref="InvalidPopulationRuleException"/> if the spec cannot be evaluated.
        /// </summary>
        public IReadOnlyList<GameInfo> Evaluate(PopulationSpec spec, IGameCatalog source = null)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            var problem = Validate(spec);
            if (problem != null)
            {
                throw new InvalidPopulationRuleException(problem);
            }

            var rule = registry.Get(spec.Source);
            if (rule.ParameterKind == RuleParameterKind.Lookup && (spec.ItemIds == null || spec.ItemIds.Count == 0))
            {
                return new GameInfo[0];
            }

            var seen = new HashSet<Guid>();
            return rule.Select(source ?? catalog, spec, clock.UtcNow)
                .Where(g => g != null && !g.IsHidden && seen.Add(g.Id))
                .OrderBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Returns why a spec cannot be evaluated, or null if it is valid.</summary>
        public string Validate(PopulationSpec spec)
        {
            if (spec == null)
            {
                return "The wheel has no population criteria.";
            }

            var rule = registry.Get(spec.Source);
            if (rule == null)
            {
                return $"Unknown population source {spec.Source}.";
            }

            if ((rule.ParameterKind == RuleParameterKind.Hours || rule.ParameterKind == RuleParameterKind.Days) && spec.Amount.HasValue)
            {
                var amount = spec.Amount.Value;
                if (double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0)
                {
                    return $"\"{rule.DisplayName}\" needs a number that is zero or more.";
                }
            }

            return null;
        }

        /// <summary>Every library aspect whose change can alter the result of this spec.</summary>
        public LibraryFields DependenciesOf(PopulationSpec spec)
        {
            var rule = spec == null ? null : registry.Get(spec.Source);
            return rule == null
                ? LibraryFields.All
                : rule.Dependencies | LibraryFields.Collection | LibraryFields.Hidden;
        }

        public string Describe(PopulationSpec spec) => registry.Get(spec.Source)?.Describe(spec, catalog) ?? spec.Source.ToString();
    }
}
