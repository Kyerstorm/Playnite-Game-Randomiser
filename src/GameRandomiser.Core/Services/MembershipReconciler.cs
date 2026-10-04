using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Services
{
    /// <summary>Why a refresh was requested. Higher values win when requests for a wheel are merged.</summary>
    public enum RefreshReason
    {
        LibraryChanged = 0,
        Startup = 1,
        RulesChanged = 2,
        Manual = 3
    }

    public enum RefreshResultKind
    {
        /// <summary>The game list changed.</summary>
        Updated,

        /// <summary>The wheel already matched its criteria.</summary>
        Unchanged,

        /// <summary>The refresh failed; the previous game list was kept.</summary>
        Failed,

        /// <summary>The wheel was edited while the refresh ran; the result was discarded and it will run again.</summary>
        Superseded,

        /// <summary>The wheel no longer exists.</summary>
        Skipped
    }

    /// <summary>An immutable copy of everything a refresh reads from a wheel.</summary>
    public sealed class WheelCapture
    {
        public Guid WheelId { get; internal set; }
        public int Revision { get; internal set; }
        public MembershipPolicy Policy { get; internal set; }
        public PopulationSpec Population { get; internal set; }
        public IReadOnlyList<Guid> GameIds { get; internal set; } = new Guid[0];
        public IReadOnlyList<Guid> PinnedGameIds { get; internal set; } = new Guid[0];
        public IReadOnlyList<Guid> ExcludedGameIds { get; internal set; } = new Guid[0];
        public SortMode SortMode { get; internal set; }
        public RefreshReason Reason { get; internal set; }

        /// <summary>
        /// Pin games that are on the wheel but don't match the criteria. Used when a manual wheel
        /// becomes "criteria + pinned", so hand-picked games aren't lost.
        /// </summary>
        public bool PinUnmatched { get; internal set; }
    }

    /// <summary>The computed result of reconciling one wheel, ready to be applied on the UI thread.</summary>
    public sealed class RefreshOutcome
    {
        private static readonly Guid[] None = new Guid[0];

        public Guid WheelId { get; internal set; }
        public int Revision { get; internal set; }
        public RefreshReason Reason { get; internal set; }
        public bool PinUnmatched { get; internal set; }
        public bool Succeeded { get; internal set; }
        public IReadOnlyList<Guid> Membership { get; internal set; } = None;
        public IReadOnlyList<Guid> NewPins { get; internal set; } = None;
        public int Added { get; internal set; }
        public int Removed { get; internal set; }
        public bool Changed { get; internal set; }
        public Exception Exception { get; internal set; }

        public static RefreshOutcome Failure(WheelCapture capture, Exception exception) => new RefreshOutcome
        {
            WheelId = capture.WheelId,
            Revision = capture.Revision,
            Reason = capture.Reason,
            PinUnmatched = capture.PinUnmatched,
            Succeeded = false,
            Exception = exception
        };
    }

    /// <summary>What happened to one wheel in a refresh, as reported to the UI.</summary>
    public sealed class WheelRefreshResult
    {
        public WheelRefreshResult(Guid wheelId, RefreshResultKind kind, RefreshReason reason)
        {
            WheelId = wheelId;
            Kind = kind;
            Reason = reason;
        }

        public Guid WheelId { get; }
        public RefreshResultKind Kind { get; }
        public RefreshReason Reason { get; }
        public int Added { get; internal set; }
        public int Removed { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public ErrorCategory Category { get; internal set; }
        public Exception Exception { get; internal set; }
    }

    /// <summary>Arranges game ids the way a wheel's sort mode asks. Random order is left untouched.</summary>
    public static class WheelOrdering
    {
        public static List<Guid> Order(IEnumerable<Guid> gameIds, SortMode mode, Func<Guid, GameInfo> resolve)
        {
            var resolved = gameIds.Select((id, index) => new { id, index, game = resolve(id) }).ToList();
            if (mode == SortMode.Alphabetical)
            {
                return resolved
                    .OrderBy(x => x.game == null ? 1 : 0)
                    .ThenBy(x => x.game?.SortKey ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.game?.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.index)
                    .Select(x => x.id)
                    .ToList();
            }

            if (mode == SortMode.Library)
            {
                return resolved
                    .OrderBy(x => x.game == null ? int.MaxValue : x.game.LibraryIndex)
                    .ThenBy(x => x.index)
                    .Select(x => x.id)
                    .ToList();
            }

            return resolved.Select(x => x.id).ToList();
        }
    }

    /// <summary>
    /// Works out what a wheel's game list should be. Pure and deterministic: the same wheel and library
    /// always give the same list, and a wheel that already matches comes back unchanged.
    /// </summary>
    public static class MembershipReconciler
    {
        /// <param name="capture">The wheel as it was when the refresh started.</param>
        /// <param name="matches">Games currently matching the wheel's criteria.</param>
        /// <param name="library">The library the criteria were evaluated against.</param>
        /// <param name="random">Only used to place new games on a randomly arranged wheel.</param>
        public static RefreshOutcome Reconcile(WheelCapture capture, IReadOnlyList<GameInfo> matches, IGameCatalog library, IRandomSource random)
        {
            var existing = capture.GameIds;
            var matchIds = new List<Guid>();
            var matchSet = new HashSet<Guid>();
            foreach (var game in matches)
            {
                if (game != null && game.Id != Guid.Empty && !game.IsHidden && matchSet.Add(game.Id))
                {
                    matchIds.Add(game.Id);
                }
            }

            var newPins = new List<Guid>();
            var desiredOrder = new List<Guid>();
            var desired = new HashSet<Guid>();
            var dynamic = capture.Policy != MembershipPolicy.ManualSnapshot;

            // Removals the user made only mean something on a dynamic wheel.
            var excluded = dynamic ? new HashSet<Guid>(capture.ExcludedGameIds) : new HashSet<Guid>();

            if (!dynamic)
            {
                // Explicit refresh of a snapshot wheel: keep everything, add what newly matches.
                foreach (var id in existing)
                {
                    if (desired.Add(id))
                    {
                        desiredOrder.Add(id);
                    }
                }
            }

            foreach (var id in matchIds)
            {
                if (!excluded.Contains(id) && desired.Add(id))
                {
                    desiredOrder.Add(id);
                }
            }

            if (capture.Policy == MembershipPolicy.CriteriaPlusPinned)
            {
                var pins = new List<Guid>(capture.PinnedGameIds);
                if (capture.PinUnmatched)
                {
                    var pinSet = new HashSet<Guid>(pins);
                    foreach (var id in existing)
                    {
                        if (!matchSet.Contains(id) && pinSet.Add(id) && IsEligible(library, id))
                        {
                            newPins.Add(id);
                            pins.Add(id);
                        }
                    }
                }

                foreach (var id in pins)
                {
                    // A pin outlives the criteria, but never a game being hidden or deleted.
                    if (id != Guid.Empty && !excluded.Contains(id) && IsEligible(library, id) && desired.Add(id))
                    {
                        desiredOrder.Add(id);
                    }
                }
            }

            // Stable ordering: games that stay keep their position; new games are then placed.
            var result = new List<Guid>(desired.Count);
            var kept = new HashSet<Guid>();
            foreach (var id in existing)
            {
                if (desired.Contains(id) && kept.Add(id))
                {
                    result.Add(id);
                }
            }

            var additions = desiredOrder.Where(id => !kept.Contains(id)).ToList();
            if (additions.Count > 0)
            {
                if (capture.SortMode == SortMode.Random)
                {
                    foreach (var id in additions)
                    {
                        result.Insert(random.NextInt(result.Count + 1), id);
                    }
                }
                else
                {
                    result.AddRange(additions);
                    result = WheelOrdering.Order(result, capture.SortMode, library.TryGet);
                }
            }

            return new RefreshOutcome
            {
                WheelId = capture.WheelId,
                Revision = capture.Revision,
                Reason = capture.Reason,
                PinUnmatched = capture.PinUnmatched,
                Succeeded = true,
                Membership = result,
                NewPins = newPins,
                Added = additions.Count,
                Removed = existing.Distinct().Count() - kept.Count,
                Changed = !result.SequenceEqual(existing)
            };
        }

        private static bool IsEligible(IGameCatalog library, Guid id)
        {
            var game = library.TryGet(id);
            return game != null && !game.IsHidden;
        }
    }
}
