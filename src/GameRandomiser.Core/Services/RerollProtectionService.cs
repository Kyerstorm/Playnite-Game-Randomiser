using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Services
{
    /// <summary>
    /// The eligibility stage that sits between a wheel's games and the random pick. It decides which
    /// games may win the next spin and whether a spin is allowed at all; it never touches the random
    /// source, so every eligible game keeps an identical chance.
    /// State is per wheel. A "session" starts with a pick and ends when the pick is accepted, the
    /// cooldown expires, or protection is reset; spins inside a session are rerolls.
    /// </summary>
    public sealed class RerollProtectionService
    {
        private readonly WheelService wheels;

        public RerollProtectionService(WheelService wheels)
        {
            this.wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        }

        /// <summary>Runs the whole pipeline: resolve the wheel's visible, existing games, then apply protection.</summary>
        public SelectionResult BuildSelection(Guid wheelId, RerollProtectionOptions options) =>
            Evaluate(wheelId, wheels.ResolveEntries(wheelId), options);

        /// <summary>Applies protection to games that were already resolved for display.</summary>
        public SelectionResult Evaluate(Guid wheelId, IReadOnlyList<GameInfo> entries, RerollProtectionOptions options) =>
            Evaluate(entries, wheels.GetWheel(wheelId)?.Reroll, options, wheels.Clock.UtcNow);

        /// <summary>Records a finished spin: the winner becomes a recent winner and the session advances.</summary>
        public void RecordSpin(Guid wheelId, Guid winnerId, RerollProtectionOptions options)
        {
            if (options == null || !options.Enabled || wheels.GetWheel(wheelId) == null)
            {
                return;
            }

            var now = wheels.Clock.UtcNow;
            wheels.UpdateWheel(wheelId, wheel => wheel.Reroll = Advance(wheel.Reroll, winnerId, options, now),
                WheelChangeKind.ProtectionChanged);
        }

        /// <summary>
        /// The user is keeping the current pick: the session ends, so the next spin is a fresh pick
        /// with all rerolls available. Recent winners stay excluded.
        /// </summary>
        public bool Accept(Guid wheelId)
        {
            var state = wheels.GetWheel(wheelId)?.Reroll;
            if (state == null || (!state.SessionId.HasValue && state.RerollCount == 0 && !state.CooldownUntilUtc.HasValue))
            {
                return false;
            }

            wheels.UpdateWheel(wheelId, wheel =>
            {
                wheel.Reroll.SessionId = null;
                wheel.Reroll.RerollCount = 0;
                wheel.Reroll.CooldownUntilUtc = null;
            }, WheelChangeKind.ProtectionChanged);
            return true;
        }

        /// <summary>Forgets recent winners, the reroll count and any cooldown for one wheel.</summary>
        public bool Reset(Guid wheelId)
        {
            var state = wheels.GetWheel(wheelId)?.Reroll;
            if (state == null)
            {
                return false;
            }

            wheels.UpdateWheel(wheelId, wheel => wheel.Reroll = null, WheelChangeKind.ProtectionChanged);
            return true;
        }

        /// <summary>Resets every wheel in one save. Returns the number of wheels that had state.</summary>
        public int ResetAll()
        {
            var ids = wheels.Wheels.Where(w => w.Reroll != null).Select(w => w.Id).ToList();
            if (ids.Count == 0)
            {
                return 0;
            }

            using (wheels.Batch())
            {
                foreach (var id in ids)
                {
                    Reset(id);
                }
            }

            return ids.Count;
        }

        /// <summary>
        /// Pure eligibility check. Nothing is mutated; invalid or expired persisted state is treated
        /// as if it had already been cleaned up.
        /// </summary>
        public static SelectionResult Evaluate(IReadOnlyList<GameInfo> entries, RerollState state, RerollProtectionOptions options, DateTime utcNow)
        {
            entries = entries ?? new GameInfo[0];
            var enabled = options != null && options.Enabled;
            var result = new SelectionResult { Entries = entries, ProtectionEnabled = enabled };

            // A game listed twice must not get two chances: only its first segment is a candidate.
            var seen = new HashSet<Guid>();
            var all = new List<int>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && seen.Add(entries[i].Id))
                {
                    all.Add(i);
                }
            }

            if (all.Count == 0)
            {
                result.BlockReason = SpinBlockReason.EmptyWheel;
                result.Message = "This wheel has no games to pick from.";
                return result;
            }

            if (!enabled)
            {
                result.EligibleIndices = all;
                return result;
            }

            var current = Effective(state, options, utcNow);
            result.SessionActive = current.SessionId.HasValue;
            if (options.LimitRerolls)
            {
                var used = result.SessionActive ? current.RerollCount : 0;
                result.MaxRerolls = options.MaxRerolls;
                result.RerollsRemaining = Math.Max(0, options.MaxRerolls - used);
                if (result.SessionActive && result.RerollsRemaining == 0)
                {
                    if (current.CooldownUntilUtc.HasValue)
                    {
                        result.BlockReason = SpinBlockReason.CoolingDown;
                        result.CooldownUntilUtc = current.CooldownUntilUtc;
                        result.Message = UserMessages.CoolingDown(current.CooldownUntilUtc.Value - utcNow);
                    }
                    else
                    {
                        result.BlockReason = SpinBlockReason.RerollLimitReached;
                        result.Message = UserMessages.RerollLimitReached;
                    }

                    return result;
                }
            }

            var excluded = new HashSet<Guid>(current.RecentWinnerIds.Take(options.ExcludedWinnerCount));
            var eligible = all.Where(i => !excluded.Contains(entries[i].Id)).ToList();
            if (eligible.Count == 0)
            {
                if (all.Count == 1)
                {
                    // Excluding the only game would make the wheel unusable, so protection steps aside, visibly.
                    result.EligibleIndices = all;
                    result.ExclusionBypassed = true;
                    result.Message = UserMessages.SingleGameBypass;
                    return result;
                }

                result.ExcludedIds = all.Select(i => entries[i].Id).ToList();
                result.BlockReason = SpinBlockReason.AllCandidatesExcluded;
                result.Message = UserMessages.AllCandidatesExcluded;
                return result;
            }

            result.EligibleIndices = eligible;
            result.ExcludedIds = all.Where(i => excluded.Contains(entries[i].Id)).Select(i => entries[i].Id).ToList();
            return result;
        }

        /// <summary>Returns the state after <paramref name="winnerId"/> wins a spin. Does not modify the input.</summary>
        public static RerollState Advance(RerollState state, Guid winnerId, RerollProtectionOptions options, DateTime utcNow)
        {
            var next = Effective(state, options, utcNow);
            if (next.SessionId.HasValue)
            {
                next.RerollCount = Math.Min(RerollProtectionOptions.MaxCount, next.RerollCount + 1);
            }
            else
            {
                next.SessionId = Guid.NewGuid();
                next.RerollCount = 0;
            }

            next.RecentWinnerIds.Remove(winnerId);
            next.RecentWinnerIds.Insert(0, winnerId);
            if (next.RecentWinnerIds.Count > RerollProtectionOptions.MaxCount)
            {
                next.RecentWinnerIds.RemoveRange(RerollProtectionOptions.MaxCount, next.RecentWinnerIds.Count - RerollProtectionOptions.MaxCount);
            }

            next.CooldownUntilUtc = options.LimitRerolls && options.CooldownEnabled && next.RerollCount >= options.MaxRerolls
                ? utcNow.AddMinutes(options.CooldownMinutes)
                : (DateTime?)null;
            return next;
        }

        /// <summary>
        /// A sanitised copy of the state as it applies right now: an expired cooldown has ended the
        /// session, and a cooldown can never be further away than the configured duration (which also
        /// neutralises hand-edited values and clock changes).
        /// </summary>
        private static RerollState Effective(RerollState state, RerollProtectionOptions options, DateTime utcNow)
        {
            var copy = new RerollState
            {
                RecentWinnerIds = new List<Guid>(state?.RecentWinnerIds ?? new List<Guid>()),
                RerollCount = state?.RerollCount ?? 0,
                CooldownUntilUtc = state?.CooldownUntilUtc,
                SessionId = state?.SessionId
            }.Sanitize();

            if (!copy.CooldownUntilUtc.HasValue)
            {
                return copy;
            }

            if (!options.LimitRerolls || !options.CooldownEnabled)
            {
                copy.CooldownUntilUtc = null;
                return copy;
            }

            var latest = utcNow.AddMinutes(options.CooldownMinutes);
            if (copy.CooldownUntilUtc.Value > latest)
            {
                copy.CooldownUntilUtc = latest;
            }

            if (copy.CooldownUntilUtc.Value <= utcNow)
            {
                copy.SessionId = null;
                copy.RerollCount = 0;
                copy.CooldownUntilUtc = null;
            }

            return copy;
        }
    }
}
