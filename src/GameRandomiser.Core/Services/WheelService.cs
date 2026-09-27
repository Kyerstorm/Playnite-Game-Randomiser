using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Services
{
    public enum WheelChangeKind
    {
        Reloaded,
        WheelAdded,
        WheelRemoved,
        WheelRenamed,
        ActiveWheelChanged,
        EntriesChanged,
        OrderChanged,
        HistoryChanged
    }

    public sealed class WheelsChangedEventArgs : EventArgs
    {
        public WheelsChangedEventArgs(WheelChangeKind kind, Guid? wheelId)
        {
            Kind = kind;
            WheelId = wheelId;
        }

        public WheelChangeKind Kind { get; }
        public Guid? WheelId { get; }
    }

    public sealed class AddGamesResult
    {
        public int Added { get; internal set; }
        public int AlreadyPresent { get; internal set; }
        public int Ineligible { get; internal set; }

        public override string ToString() => $"Added {Added}, already present {AlreadyPresent}, ineligible {Ineligible}";
    }

    /// <summary>
    /// Owns the wheel collection: create/rename/delete/switch, membership and ordering.
    /// Every mutation is persisted immediately and raises <see cref="Changed"/>.
    /// Not thread-safe: call from the UI thread.
    /// </summary>
    public sealed class WheelService
    {
        public const int MaxNameLength = 60;

        private readonly IRandomiserStore store;
        private readonly IGameCatalog catalog;
        private readonly IRandomSource random;
        private readonly IClock clock;
        private RandomiserData data;

        public WheelService(IRandomiserStore store, IGameCatalog catalog, IRandomSource random, IClock clock = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.clock = clock ?? new SystemClock();
            var result = store.Load();
            data = result.Data;
            LoadWarning = result.Warning;
        }

        public event EventHandler<WheelsChangedEventArgs> Changed;

        /// <summary>Set when persisted data had to be recovered on load.</summary>
        public string LoadWarning { get; }

        public IReadOnlyList<RandomiserWheel> Wheels => data.Wheels.AsReadOnly();

        public RandomiserWheel ActiveWheel =>
            (data.ActiveWheelId.HasValue ? GetWheel(data.ActiveWheelId.Value) : null) ?? data.Wheels.FirstOrDefault();

        internal IClock Clock => clock;

        public RandomiserWheel GetWheel(Guid wheelId) => data.Wheels.FirstOrDefault(w => w.Id == wheelId);

        public RandomiserWheel CreateWheel(
            string name,
            IEnumerable<Guid> gameIds = null,
            SortMode sortMode = SortMode.Alphabetical,
            PopulationSpec population = null,
            string icon = null,
            bool makeActive = true)
        {
            var cleanName = ValidateName(name, null);
            var wheel = new RandomiserWheel
            {
                Name = cleanName,
                Icon = string.IsNullOrWhiteSpace(icon) ? "🎲" : icon,
                SortMode = sortMode,
                Population = population?.Clone(),
                CreatedUtc = clock.UtcNow,
                ModifiedUtc = clock.UtcNow
            };

            data.Wheels.Add(wheel);
            if (gameIds != null)
            {
                AddGamesInternal(wheel, gameIds);
            }

            if (makeActive || data.Wheels.Count == 1)
            {
                data.ActiveWheelId = wheel.Id;
            }

            Commit(WheelChangeKind.WheelAdded, wheel.Id);
            return wheel;
        }

        public void RenameWheel(Guid wheelId, string newName)
        {
            var wheel = RequireWheel(wheelId);
            var cleanName = ValidateName(newName, wheelId);
            if (wheel.Name == cleanName)
            {
                return;
            }

            wheel.Name = cleanName;
            Touch(wheel);
            Commit(WheelChangeKind.WheelRenamed, wheelId);
        }

        public void SetWheelIcon(Guid wheelId, string icon)
        {
            var wheel = RequireWheel(wheelId);
            wheel.Icon = string.IsNullOrWhiteSpace(icon) ? "🎲" : icon.Trim();
            Touch(wheel);
            Commit(WheelChangeKind.WheelRenamed, wheelId);
        }

        public bool DeleteWheel(Guid wheelId)
        {
            var wheel = GetWheel(wheelId);
            if (wheel == null)
            {
                return false;
            }

            var index = data.Wheels.IndexOf(wheel);
            data.Wheels.Remove(wheel);
            if (data.ActiveWheelId == wheelId)
            {
                data.ActiveWheelId = data.Wheels.Count == 0
                    ? (Guid?)null
                    : data.Wheels[Math.Min(index, data.Wheels.Count - 1)].Id;
            }

            Commit(WheelChangeKind.WheelRemoved, wheelId);
            return true;
        }

        /// <summary>Copies name, games, ordering and criteria. History is intentionally not copied.</summary>
        public RandomiserWheel DuplicateWheel(Guid wheelId)
        {
            var source = RequireWheel(wheelId);
            var copy = new RandomiserWheel
            {
                Name = MakeUniqueName(source.Name + " (copy)"),
                Icon = source.Icon,
                GameIds = new List<Guid>(source.GameIds),
                SortMode = source.SortMode,
                Population = source.Population?.Clone(),
                IsDynamic = source.IsDynamic,
                CreatedUtc = clock.UtcNow,
                ModifiedUtc = clock.UtcNow
            };

            data.Wheels.Insert(data.Wheels.IndexOf(source) + 1, copy);
            data.ActiveWheelId = copy.Id;
            Commit(WheelChangeKind.WheelAdded, copy.Id);
            return copy;
        }

        public void SetActiveWheel(Guid wheelId)
        {
            RequireWheel(wheelId);
            if (data.ActiveWheelId == wheelId)
            {
                return;
            }

            data.ActiveWheelId = wheelId;
            Commit(WheelChangeKind.ActiveWheelChanged, wheelId);
        }

        public AddGamesResult AddGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            var result = AddGamesInternal(wheel, gameIds ?? Enumerable.Empty<Guid>());
            if (result.Added > 0)
            {
                Touch(wheel);
                Commit(WheelChangeKind.EntriesChanged, wheelId);
            }

            return result;
        }

        public int RemoveGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            var toRemove = new HashSet<Guid>(gameIds ?? Enumerable.Empty<Guid>());
            var removed = wheel.GameIds.RemoveAll(toRemove.Contains);
            if (removed > 0)
            {
                Touch(wheel);
                Commit(WheelChangeKind.EntriesChanged, wheelId);
            }

            return removed;
        }

        public int ClearGames(Guid wheelId)
        {
            var wheel = RequireWheel(wheelId);
            var count = wheel.GameIds.Count;
            if (count == 0)
            {
                return 0;
            }

            wheel.GameIds.Clear();
            Touch(wheel);
            Commit(WheelChangeKind.EntriesChanged, wheelId);
            return count;
        }

        public bool Contains(Guid wheelId, Guid gameId) => GetWheel(wheelId)?.GameIds.Contains(gameId) == true;

        public IReadOnlyList<RandomiserWheel> WheelsContaining(Guid gameId) =>
            data.Wheels.Where(w => w.GameIds.Contains(gameId)).ToList();

        /// <summary>Changes the arrangement mode and immediately re-arranges. Random re-shuffles.</summary>
        public void SetSortMode(Guid wheelId, SortMode mode)
        {
            var wheel = RequireWheel(wheelId);
            wheel.SortMode = mode;
            if (mode == SortMode.Random)
            {
                ShuffleInPlace(wheel.GameIds);
            }
            else
            {
                ApplyOrdering(wheel);
            }

            Touch(wheel);
            Commit(WheelChangeKind.OrderChanged, wheelId);
        }

        /// <summary>
        /// Randomises the physical arrangement only. Does not pick a winner, is not a spin,
        /// and never touches history.
        /// </summary>
        public void Shuffle(Guid wheelId)
        {
            var wheel = RequireWheel(wheelId);
            ShuffleInPlace(wheel.GameIds);
            wheel.SortMode = SortMode.Random;
            Touch(wheel);
            Commit(WheelChangeKind.OrderChanged, wheelId);
        }

        /// <summary>Resolves the wheel's eligible games in wheel order, skipping missing or hidden ones.</summary>
        public IReadOnlyList<GameInfo> ResolveEntries(Guid wheelId)
        {
            var wheel = GetWheel(wheelId);
            if (wheel == null)
            {
                return new GameInfo[0];
            }

            var result = new List<GameInfo>(wheel.GameIds.Count);
            foreach (var id in wheel.GameIds)
            {
                var game = catalog.TryGet(id);
                if (game != null && !game.IsHidden)
                {
                    result.Add(game);
                }
            }

            return result;
        }

        /// <summary>Removes entries whose game is deleted or hidden from every wheel. Returns count removed.</summary>
        public int CleanupInvalidEntries()
        {
            var total = 0;
            var changed = new List<Guid>();
            foreach (var wheel in data.Wheels)
            {
                var removed = wheel.GameIds.RemoveAll(id => !catalog.IsEligible(id));
                if (removed > 0)
                {
                    total += removed;
                    Touch(wheel);
                    changed.Add(wheel.Id);
                }
            }

            if (total > 0)
            {
                Commit(WheelChangeKind.EntriesChanged, changed.Count == 1 ? changed[0] : (Guid?)null);
            }

            return total;
        }

        /// <summary>Removes one game from every wheel (library removal / hidden).</summary>
        public int RemoveGameFromAllWheels(Guid gameId) => RemoveGamesFromAllWheels(new[] { gameId });

        public int RemoveGamesFromAllWheels(IEnumerable<Guid> gameIds)
        {
            var set = new HashSet<Guid>(gameIds);
            var total = 0;
            foreach (var wheel in data.Wheels)
            {
                var removed = wheel.GameIds.RemoveAll(set.Contains);
                if (removed > 0)
                {
                    total += removed;
                    Touch(wheel);
                }
            }

            if (total > 0)
            {
                Commit(WheelChangeKind.EntriesChanged, null);
            }

            return total;
        }

        /// <summary>Re-orders any wheel using A-Z or Library order (e.g. after games were renamed).</summary>
        public void ReapplyOrdering(Guid wheelId)
        {
            var wheel = RequireWheel(wheelId);
            if (wheel.SortMode == SortMode.Random)
            {
                return;
            }

            ApplyOrdering(wheel);
            Commit(WheelChangeKind.OrderChanged, wheelId);
        }

        public string MakeUniqueName(string baseName)
        {
            var name = Truncate((baseName ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(name))
            {
                name = "New wheel";
            }

            if (!NameExists(name, null))
            {
                return name;
            }

            for (var i = 2; ; i++)
            {
                var candidate = Truncate(name, 6) + " " + i;
                if (!NameExists(candidate, null))
                {
                    return candidate;
                }
            }
        }

        /// <summary>Returns the trimmed name, or throws ArgumentException with a user-facing message.</summary>
        public string ValidateName(string name, Guid? existingWheelId)
        {
            var clean = (name ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                throw new ArgumentException("Please enter a name for the wheel.");
            }

            if (clean.Length > MaxNameLength)
            {
                throw new ArgumentException($"Wheel names can be at most {MaxNameLength} characters.");
            }

            if (NameExists(clean, existingWheelId))
            {
                throw new ArgumentException($"A wheel named \"{clean}\" already exists.");
            }

            return clean;
        }

        /// <summary>Applies a mutation to a wheel and persists it. Used by HistoryService.</summary>
        internal void UpdateWheel(Guid wheelId, Action<RandomiserWheel> mutation, WheelChangeKind kind)
        {
            var wheel = RequireWheel(wheelId);
            mutation(wheel);
            Commit(kind, wheelId);
        }

        internal IEnumerable<RandomiserWheel> AllWheelsMutable => data.Wheels;

        internal void CommitExternal(WheelChangeKind kind, Guid? wheelId) => Commit(kind, wheelId);

        private AddGamesResult AddGamesInternal(RandomiserWheel wheel, IEnumerable<Guid> gameIds)
        {
            var result = new AddGamesResult();
            var existing = new HashSet<Guid>(wheel.GameIds);
            var toAdd = new List<Guid>();
            foreach (var id in gameIds)
            {
                if (existing.Contains(id))
                {
                    result.AlreadyPresent++;
                    continue;
                }

                if (!catalog.IsEligible(id))
                {
                    result.Ineligible++;
                    continue;
                }

                existing.Add(id);
                toAdd.Add(id);
            }

            if (toAdd.Count == 0)
            {
                return result;
            }

            if (wheel.SortMode == SortMode.Random)
            {
                foreach (var id in toAdd)
                {
                    wheel.GameIds.Insert(random.NextInt(wheel.GameIds.Count + 1), id);
                }
            }
            else
            {
                wheel.GameIds.AddRange(toAdd);
                ApplyOrdering(wheel);
            }

            result.Added = toAdd.Count;
            return result;
        }

        private void ApplyOrdering(RandomiserWheel wheel)
        {
            if (wheel.SortMode == SortMode.Random)
            {
                return;
            }

            var resolved = wheel.GameIds.Select((id, index) => new { id, index, game = catalog.TryGet(id) }).ToList();
            IEnumerable<Guid> ordered;
            if (wheel.SortMode == SortMode.Alphabetical)
            {
                ordered = resolved
                    .OrderBy(x => x.game == null ? 1 : 0)
                    .ThenBy(x => x.game?.SortKey ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.game?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.index)
                    .Select(x => x.id);
            }
            else
            {
                ordered = resolved
                    .OrderBy(x => x.game == null ? int.MaxValue : x.game.LibraryIndex)
                    .ThenBy(x => x.index)
                    .Select(x => x.id);
            }

            wheel.GameIds = ordered.ToList();
        }

        private void ShuffleInPlace(IList<Guid> list) => RandomiserService.ShuffleInPlace(list, random);

        private bool NameExists(string name, Guid? exceptWheelId) =>
            data.Wheels.Any(w => w.Id != exceptWheelId && string.Equals(w.Name, name, StringComparison.CurrentCultureIgnoreCase));

        private static string Truncate(string name, int reserve = 0)
        {
            var max = MaxNameLength - reserve;
            return name.Length > max ? name.Substring(0, max).TrimEnd() : name;
        }

        private RandomiserWheel RequireWheel(Guid wheelId) =>
            GetWheel(wheelId) ?? throw new KeyNotFoundException("The selected wheel no longer exists.");

        private void Touch(RandomiserWheel wheel) => wheel.ModifiedUtc = clock.UtcNow;

        private void Commit(WheelChangeKind kind, Guid? wheelId)
        {
            store.Save(data);
            Changed?.Invoke(this, new WheelsChangedEventArgs(kind, wheelId));
        }
    }
}
