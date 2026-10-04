using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
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
        HistoryChanged,

        /// <summary>Policy, criteria, pins or removed-games list changed: the wheel needs reconciling.</summary>
        MembershipRulesChanged,

        /// <summary>Only the refresh status of a wheel changed.</summary>
        RefreshStateChanged,

        /// <summary>Only the reroll protection state of a wheel changed.</summary>
        ProtectionChanged
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

    public sealed class SaveFailedEventArgs : EventArgs
    {
        public SaveFailedEventArgs(Exception exception) => Exception = exception;

        public Exception Exception { get; }
    }

    public sealed class AddGamesResult
    {
        public int Added { get; internal set; }
        public int AlreadyPresent { get; internal set; }
        public int Ineligible { get; internal set; }

        /// <summary>Games not added because the wheel only holds games matching its criteria.</summary>
        public int NotMatching { get; internal set; }

        public override string ToString() =>
            $"Added {Added}, already present {AlreadyPresent}, ineligible {Ineligible}, not matching {NotMatching}";
    }

    /// <summary>The refresh state of a wheel as shown to the user.</summary>
    public sealed class RefreshInfo
    {
        public RefreshInfo(RefreshStatus status, DateTime? lastRefreshUtc, string error)
        {
            Status = status;
            LastRefreshUtc = lastRefreshUtc;
            Error = error;
        }

        public RefreshStatus Status { get; }
        public DateTime? LastRefreshUtc { get; }
        public string Error { get; }

        /// <summary>True when the list shown may no longer reflect the library.</summary>
        public bool IsStale => Status == RefreshStatus.Failed || Status == RefreshStatus.FailedUsingLastKnownGood;
    }

    /// <summary>
    /// Owns the wheel collection: create/rename/delete/switch, membership and ordering.
    /// Every mutation is persisted immediately and raises <see cref="Changed"/>.
    /// A failed save never throws: the change stays in memory, <see cref="SaveFailed"/> is raised and
    /// the next successful save writes everything.
    /// Not thread-safe: call from the UI thread.
    /// </summary>
    public sealed class WheelService
    {
        public const int MaxNameLength = 60;

        private readonly IRandomiserStore store;
        private readonly IGameCatalog catalog;
        private readonly IRandomSource random;
        private readonly IClock clock;
        private readonly HashSet<Guid> refreshing = new HashSet<Guid>();
        private readonly List<PendingChange> batched = new List<PendingChange>();
        private RandomiserData data;
        private int batchDepth;

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

        /// <summary>A save failed. The previous file is intact and the change is retried with the next save.</summary>
        public event EventHandler<SaveFailedEventArgs> SaveFailed;

        /// <summary>Set when persisted data had to be recovered on load.</summary>
        public string LoadWarning { get; }

        /// <summary>The most recent save failure, or null once a save has succeeded again.</summary>
        public Exception LastSaveError { get; private set; }

        public bool HasUnsavedChanges => LastSaveError != null;

        public IReadOnlyList<RandomiserWheel> Wheels => data.Wheels.AsReadOnly();

        public RandomiserWheel ActiveWheel =>
            (data.ActiveWheelId.HasValue ? GetWheel(data.ActiveWheelId.Value) : null) ?? data.Wheels.FirstOrDefault();

        internal IClock Clock => clock;

        public RandomiserWheel GetWheel(Guid wheelId) => data.Wheels.FirstOrDefault(w => w.Id == wheelId);

        /// <summary>
        /// Groups several mutations into one save. Change events are raised when the outermost batch ends.
        /// </summary>
        public IDisposable Batch()
        {
            batchDepth++;
            return new BatchScope(this);
        }

        public RandomiserWheel CreateWheel(
            string name,
            IEnumerable<Guid> gameIds = null,
            SortMode sortMode = SortMode.Alphabetical,
            PopulationSpec population = null,
            string icon = null,
            bool makeActive = true,
            MembershipPolicy policy = MembershipPolicy.ManualSnapshot)
        {
            var cleanName = ValidateName(name, null);
            var wheel = new RandomiserWheel
            {
                Name = cleanName,
                Icon = string.IsNullOrWhiteSpace(icon) ? "🎲" : icon,
                SortMode = sortMode,
                Population = population?.Clone(),
                MembershipPolicy = population == null ? MembershipPolicy.ManualSnapshot : policy,
                CreatedUtc = clock.UtcNow,
                ModifiedUtc = clock.UtcNow
            };

            data.Wheels.Add(wheel);
            if (gameIds != null)
            {
                AddGamesInternal(wheel, gameIds);
            }

            if (wheel.IsDynamic)
            {
                // The caller populated the wheel from a fresh evaluation of its criteria.
                wheel.LastRefreshUtc = clock.UtcNow;
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
            wheel.ModifiedUtc = clock.UtcNow;
            Commit(WheelChangeKind.WheelRenamed, wheelId);
        }

        public void SetWheelIcon(Guid wheelId, string icon)
        {
            var wheel = RequireWheel(wheelId);
            wheel.Icon = string.IsNullOrWhiteSpace(icon) ? "🎲" : icon.Trim();
            wheel.ModifiedUtc = clock.UtcNow;
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
            refreshing.Remove(wheelId);
            if (data.ActiveWheelId == wheelId)
            {
                data.ActiveWheelId = data.Wheels.Count == 0
                    ? (Guid?)null
                    : data.Wheels[Math.Min(index, data.Wheels.Count - 1)].Id;
            }

            Commit(WheelChangeKind.WheelRemoved, wheelId);
            return true;
        }

        /// <summary>
        /// Copies name, games, ordering, criteria and membership policy.
        /// History and reroll protection state are intentionally not copied.
        /// </summary>
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
                MembershipPolicy = source.MembershipPolicy,
                PinnedGameIds = new List<Guid>(source.PinnedGameIds),
                ExcludedGameIds = new List<Guid>(source.ExcludedGameIds),
                LastRefreshUtc = source.LastRefreshUtc,
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

        /// <summary>
        /// Adds games. On a manual wheel they are simply added. On a "criteria + pinned" wheel new games
        /// are pinned so they survive refreshes. On a strict wheel only games the user previously removed
        /// can be put back; anything else is reported as <see cref="AddGamesResult.NotMatching"/>.
        /// </summary>
        public AddGamesResult AddGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            var ids = (gameIds ?? Enumerable.Empty<Guid>()).ToList();
            if (!wheel.IsDynamic)
            {
                var plain = AddGamesInternal(wheel, ids);
                if (plain.Added > 0)
                {
                    Touch(wheel);
                    Commit(WheelChangeKind.EntriesChanged, wheelId);
                }

                return plain;
            }

            var result = new AddGamesResult();
            var present = new HashSet<Guid>(wheel.GameIds);
            var excluded = new HashSet<Guid>(wheel.ExcludedGameIds);
            var pinned = new HashSet<Guid>(wheel.PinnedGameIds);
            var toAdd = new List<Guid>();
            var rulesChanged = false;
            foreach (var id in ids.Distinct())
            {
                if (present.Contains(id))
                {
                    result.AlreadyPresent++;
                }
                else if (!catalog.IsEligible(id))
                {
                    result.Ineligible++;
                }
                else if (excluded.Contains(id))
                {
                    // Undo of an earlier removal: the game goes back exactly as it was.
                    wheel.ExcludedGameIds.Remove(id);
                    toAdd.Add(id);
                    rulesChanged = true;
                }
                else if (wheel.MembershipPolicy == MembershipPolicy.StrictCriteria)
                {
                    result.NotMatching++;
                }
                else
                {
                    if (pinned.Add(id))
                    {
                        wheel.PinnedGameIds.Add(id);
                        rulesChanged = true;
                    }

                    toAdd.Add(id);
                }
            }

            result.Added = AddGamesInternal(wheel, toAdd).Added;
            if (result.Added > 0 || rulesChanged)
            {
                Touch(wheel);
                using (Batch())
                {
                    Commit(WheelChangeKind.EntriesChanged, wheelId);
                    if (rulesChanged)
                    {
                        Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Removes games. On a dynamic wheel the removal is remembered, so a game stays off the wheel
        /// even while it still matches the criteria (until it is added back or removals are restored).
        /// </summary>
        public int RemoveGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            var toRemove = new HashSet<Guid>(gameIds ?? Enumerable.Empty<Guid>());
            var removedIds = wheel.GameIds.Where(toRemove.Contains).ToList();
            var removed = wheel.GameIds.RemoveAll(toRemove.Contains);
            if (removed > 0)
            {
                RememberRemoval(wheel, removedIds);
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

            RememberRemoval(wheel, wheel.GameIds);
            wheel.GameIds.Clear();
            Touch(wheel);
            Commit(WheelChangeKind.EntriesChanged, wheelId);
            return count;
        }

        public bool Contains(Guid wheelId, Guid gameId) => GetWheel(wheelId)?.GameIds.Contains(gameId) == true;

        public IReadOnlyList<RandomiserWheel> WheelsContaining(Guid gameId) =>
            data.Wheels.Where(w => w.GameIds.Contains(gameId)).ToList();

        // ---- Membership policy ----

        /// <summary>Changes how a criteria wheel keeps its list. Dynamic policies require criteria.</summary>
        public void SetMembershipPolicy(Guid wheelId, MembershipPolicy policy)
        {
            var wheel = RequireWheel(wheelId);
            if (!Enum.IsDefined(typeof(MembershipPolicy), policy))
            {
                throw new ArgumentException("Unknown membership policy.");
            }

            if (policy != MembershipPolicy.ManualSnapshot && wheel.Population == null)
            {
                throw new ArgumentException(UserMessages.NoCriteria);
            }

            if (wheel.MembershipPolicy == policy)
            {
                return;
            }

            wheel.MembershipPolicy = policy;
            if (policy == MembershipPolicy.ManualSnapshot)
            {
                wheel.RefreshError = null;
                refreshing.Remove(wheelId);
            }

            Touch(wheel);
            Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
        }

        /// <summary>Replaces the wheel's criteria. Passing null makes it a plain manual wheel.</summary>
        public void SetPopulation(Guid wheelId, PopulationSpec population)
        {
            var wheel = RequireWheel(wheelId);
            wheel.Population = population?.Clone();
            if (population == null)
            {
                wheel.MembershipPolicy = MembershipPolicy.ManualSnapshot;
                wheel.RefreshError = null;
            }

            Touch(wheel);
            Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
        }

        public bool IsPinned(Guid wheelId, Guid gameId) => GetWheel(wheelId)?.PinnedGameIds.Contains(gameId) == true;

        /// <summary>Pins games so they stay on a "criteria + pinned" wheel when they stop matching.</summary>
        public int PinGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            if (wheel.MembershipPolicy != MembershipPolicy.CriteriaPlusPinned || wheel.Population == null)
            {
                throw new ArgumentException(UserMessages.PinRequiresPolicy);
            }

            var pinned = new HashSet<Guid>(wheel.PinnedGameIds);
            var added = 0;
            foreach (var id in (gameIds ?? Enumerable.Empty<Guid>()).Distinct())
            {
                if (id != Guid.Empty && catalog.TryGet(id) != null && pinned.Add(id))
                {
                    wheel.PinnedGameIds.Add(id);
                    added++;
                }
            }

            if (added > 0)
            {
                Touch(wheel);
                Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
            }

            return added;
        }

        /// <summary>Unpins games. They leave the wheel at the next refresh unless they match the criteria.</summary>
        public int UnpinGames(Guid wheelId, IEnumerable<Guid> gameIds)
        {
            var wheel = RequireWheel(wheelId);
            var set = new HashSet<Guid>(gameIds ?? Enumerable.Empty<Guid>());
            var removed = wheel.PinnedGameIds.RemoveAll(set.Contains);
            if (removed > 0)
            {
                Touch(wheel);
                Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
            }

            return removed;
        }

        /// <summary>Forgets every removal on a dynamic wheel, so matching games return at the next refresh.</summary>
        public int RestoreRemovedGames(Guid wheelId)
        {
            var wheel = RequireWheel(wheelId);
            var count = wheel.ExcludedGameIds.Count;
            if (count > 0)
            {
                wheel.ExcludedGameIds.Clear();
                Touch(wheel);
                Commit(WheelChangeKind.MembershipRulesChanged, wheelId);
            }

            return count;
        }

        public RefreshInfo GetRefreshInfo(Guid wheelId)
        {
            var wheel = GetWheel(wheelId);
            if (wheel == null || !wheel.IsDynamic)
            {
                return new RefreshInfo(RefreshStatus.NotApplicable, null, null);
            }

            RefreshStatus status;
            if (refreshing.Contains(wheelId))
            {
                status = RefreshStatus.Refreshing;
            }
            else if (wheel.RefreshError != null)
            {
                status = wheel.LastRefreshUtc.HasValue ? RefreshStatus.FailedUsingLastKnownGood : RefreshStatus.Failed;
            }
            else
            {
                status = wheel.LastRefreshUtc.HasValue ? RefreshStatus.Refreshed : RefreshStatus.Ready;
            }

            return new RefreshInfo(status, wheel.LastRefreshUtc, wheel.RefreshError);
        }

        // ---- Ordering ----

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

                // Pins and remembered removals survive a game being hidden, but not it leaving the library.
                var forgotten = wheel.PinnedGameIds.RemoveAll(id => catalog.TryGet(id) == null)
                    + wheel.ExcludedGameIds.RemoveAll(id => catalog.TryGet(id) == null);
                if (removed > 0 || forgotten > 0)
                {
                    total += removed;
                    Touch(wheel);
                    changed.Add(wheel.Id);
                }
            }

            if (changed.Count > 0)
            {
                Commit(WheelChangeKind.EntriesChanged, changed.Count == 1 ? changed[0] : (Guid?)null);
            }

            return total;
        }

        /// <summary>Removes one game from every wheel (library removal / hidden).</summary>
        public int RemoveGameFromAllWheels(Guid gameId) => RemoveGamesFromAllWheels(new[] { gameId });

        /// <summary>
        /// Takes games off every wheel's current list (e.g. they were hidden). Pins are kept, so a
        /// pinned game returns if it becomes visible again.
        /// </summary>
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

        /// <summary>
        /// Drops every reference to games that left the library: membership, pins, remembered removals
        /// and recent-winner protection. Returns the number of membership entries removed.
        /// </summary>
        public int ForgetGames(IEnumerable<Guid> gameIds)
        {
            var set = new HashSet<Guid>(gameIds ?? Enumerable.Empty<Guid>());
            if (set.Count == 0)
            {
                return 0;
            }

            var total = 0;
            var any = false;
            foreach (var wheel in data.Wheels)
            {
                var removed = wheel.GameIds.RemoveAll(set.Contains);
                var other = wheel.PinnedGameIds.RemoveAll(set.Contains)
                    + wheel.ExcludedGameIds.RemoveAll(set.Contains)
                    + (wheel.Reroll?.RecentWinnerIds.RemoveAll(set.Contains) ?? 0);
                if (removed > 0 || other > 0)
                {
                    total += removed;
                    any = true;
                    Touch(wheel);
                }
            }

            if (any)
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
            Touch(wheel);
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

        /// <summary>Applies a mutation to a wheel and persists it. Used by the history and reroll services.</summary>
        internal void UpdateWheel(Guid wheelId, Action<RandomiserWheel> mutation, WheelChangeKind kind)
        {
            var wheel = RequireWheel(wheelId);
            mutation(wheel);
            Commit(kind, wheelId);
        }

        internal IEnumerable<RandomiserWheel> AllWheelsMutable => data.Wheels;

        internal void CommitExternal(WheelChangeKind kind, Guid? wheelId) => Commit(kind, wheelId);

        // ---- Refresh plumbing (used by RefreshCoordinator) ----

        /// <summary>Copies everything a refresh reads, so it can be computed away from the UI thread.</summary>
        internal WheelCapture Capture(Guid wheelId, RefreshReason reason, bool pinUnmatched)
        {
            var wheel = GetWheel(wheelId);
            if (wheel == null || wheel.Population == null)
            {
                return null;
            }

            return new WheelCapture
            {
                WheelId = wheel.Id,
                Revision = wheel.Revision,
                Policy = wheel.MembershipPolicy,
                Population = wheel.Population.Clone(),
                GameIds = wheel.GameIds.ToArray(),
                PinnedGameIds = wheel.PinnedGameIds.ToArray(),
                ExcludedGameIds = wheel.ExcludedGameIds.ToArray(),
                SortMode = wheel.SortMode,
                Reason = reason,
                PinUnmatched = pinUnmatched
            };
        }

        internal void MarkRefreshing(IEnumerable<Guid> wheelIds)
        {
            foreach (var id in wheelIds)
            {
                var wheel = GetWheel(id);
                if (wheel != null && wheel.IsDynamic && refreshing.Add(id))
                {
                    Notify(WheelChangeKind.RefreshStateChanged, id);
                }
            }
        }

        /// <summary>
        /// Applies a computed refresh. A failure never touches the game list; a result computed against
        /// an older revision of the wheel is discarded. The file is only written when the list or the
        /// success/failure state actually changed.
        /// </summary>
        internal WheelRefreshResult ApplyRefresh(RefreshOutcome outcome)
        {
            var wheel = GetWheel(outcome.WheelId);
            if (wheel == null)
            {
                return new WheelRefreshResult(outcome.WheelId, RefreshResultKind.Skipped, outcome.Reason);
            }

            var wasRefreshing = refreshing.Remove(wheel.Id);
            if (wheel.Revision != outcome.Revision)
            {
                if (wasRefreshing)
                {
                    Notify(WheelChangeKind.RefreshStateChanged, wheel.Id);
                }

                return new WheelRefreshResult(wheel.Id, RefreshResultKind.Superseded, outcome.Reason);
            }

            if (!outcome.Succeeded)
            {
                var message = UserMessages.ForRefreshFailure(outcome.Exception, out var category);
                if (wheel.IsDynamic)
                {
                    var alreadyFailed = wheel.RefreshError != null;
                    wheel.RefreshError = message;
                    if (alreadyFailed)
                    {
                        Notify(WheelChangeKind.RefreshStateChanged, wheel.Id);
                    }
                    else
                    {
                        Commit(WheelChangeKind.RefreshStateChanged, wheel.Id);
                    }
                }

                return new WheelRefreshResult(wheel.Id, RefreshResultKind.Failed, outcome.Reason)
                {
                    ErrorMessage = message,
                    Category = category,
                    Exception = outcome.Exception
                };
            }

            var stateChanged = false;
            if (wheel.IsDynamic)
            {
                stateChanged = wheel.RefreshError != null || !wheel.LastRefreshUtc.HasValue;
                wheel.RefreshError = null;
                wheel.LastRefreshUtc = clock.UtcNow;
            }

            var pinned = new HashSet<Guid>(wheel.PinnedGameIds);
            foreach (var id in outcome.NewPins)
            {
                if (pinned.Add(id))
                {
                    wheel.PinnedGameIds.Add(id);
                    stateChanged = true;
                }
            }

            if (outcome.Changed)
            {
                wheel.GameIds = new List<Guid>(outcome.Membership);
                Touch(wheel);
                Commit(WheelChangeKind.EntriesChanged, wheel.Id);
            }
            else if (stateChanged)
            {
                Commit(WheelChangeKind.RefreshStateChanged, wheel.Id);
            }
            else
            {
                Notify(WheelChangeKind.RefreshStateChanged, wheel.Id);
            }

            return new WheelRefreshResult(wheel.Id, outcome.Changed ? RefreshResultKind.Updated : RefreshResultKind.Unchanged, outcome.Reason)
            {
                Added = outcome.Added,
                Removed = outcome.Removed
            };
        }

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

        private static void RememberRemoval(RandomiserWheel wheel, IEnumerable<Guid> removedIds)
        {
            if (!wheel.IsDynamic)
            {
                return;
            }

            var excluded = new HashSet<Guid>(wheel.ExcludedGameIds);
            foreach (var id in removedIds)
            {
                if (excluded.Add(id))
                {
                    wheel.ExcludedGameIds.Add(id);
                }
            }
        }

        private void ApplyOrdering(RandomiserWheel wheel)
        {
            if (wheel.SortMode != SortMode.Random)
            {
                wheel.GameIds = WheelOrdering.Order(wheel.GameIds, wheel.SortMode, catalog.TryGet);
            }
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

        /// <summary>Marks a change to anything a refresh reads (membership, order, policy, criteria, pins).</summary>
        private void Touch(RandomiserWheel wheel)
        {
            wheel.ModifiedUtc = clock.UtcNow;
            wheel.Revision++;
        }

        private void Commit(WheelChangeKind kind, Guid? wheelId) => Publish(kind, wheelId, true);

        /// <summary>Raises a change without writing: for state that is not worth a disk write on its own.</summary>
        private void Notify(WheelChangeKind kind, Guid? wheelId) => Publish(kind, wheelId, false);

        private void Publish(WheelChangeKind kind, Guid? wheelId, bool save)
        {
            if (batchDepth > 0)
            {
                var last = batched.Count > 0 ? batched[batched.Count - 1] : null;
                if (last != null && last.Kind == kind && last.WheelId == wheelId)
                {
                    last.Save |= save;
                }
                else
                {
                    batched.Add(new PendingChange { Kind = kind, WheelId = wheelId, Save = save });
                }

                return;
            }

            if (save)
            {
                Save();
            }

            Changed?.Invoke(this, new WheelsChangedEventArgs(kind, wheelId));
        }

        private void EndBatch()
        {
            if (--batchDepth > 0 || batched.Count == 0)
            {
                return;
            }

            var changes = batched.ToList();
            batched.Clear();
            if (changes.Any(c => c.Save))
            {
                Save();
            }

            foreach (var change in changes)
            {
                Changed?.Invoke(this, new WheelsChangedEventArgs(change.Kind, change.WheelId));
            }
        }

        private void Save()
        {
            try
            {
                store.Save(data);
                LastSaveError = null;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // The store writes atomically, so the previous file is intact. Keep working from memory
                // and let whoever is listening tell the user; the next commit retries the whole document.
                LastSaveError = e;
                SaveFailed?.Invoke(this, new SaveFailedEventArgs(e));
            }
        }

        private sealed class PendingChange
        {
            public WheelChangeKind Kind;
            public Guid? WheelId;
            public bool Save;
        }

        private sealed class BatchScope : IDisposable
        {
            private WheelService owner;

            public BatchScope(WheelService owner) => this.owner = owner;

            public void Dispose()
            {
                var service = owner;
                owner = null;
                service?.EndBatch();
            }
        }
    }
}
