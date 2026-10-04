using System;
using System.Collections.Generic;
using System.Linq;

namespace GameRandomiser.Core.Models
{
    /// <summary>
    /// Global reroll protection configuration. Protection only ever narrows the candidate list;
    /// every remaining candidate keeps exactly the same chance of winning.
    /// </summary>
    public sealed class RerollProtectionOptions
    {
        public const int MinCount = 1;
        public const int MaxCount = 10;
        public const int MinCooldownMinutes = 1;
        public const int MaxCooldownMinutes = 60;

        /// <summary>Off by default, so upgrading never changes how a wheel picks.</summary>
        public bool Enabled { get; set; }

        public RerollExclusionMode Exclusion { get; set; } = RerollExclusionMode.PreviousWinner;

        /// <summary>How many recent winners <see cref="RerollExclusionMode.RecentWinners"/> leaves out.</summary>
        public int RecentWinnerCount { get; set; } = 3;

        public bool LimitRerolls { get; set; }

        /// <summary>Rerolls allowed after the first pick before one has to be accepted.</summary>
        public int MaxRerolls { get; set; } = 3;

        /// <summary>When the limit is reached, allow rerolls again after <see cref="CooldownMinutes"/>.</summary>
        public bool CooldownEnabled { get; set; }

        public int CooldownMinutes { get; set; } = 10;

        public RerollResetBehaviour ResetBehaviour { get; set; } = RerollResetBehaviour.ResetOnRestart;

        /// <summary>Number of most recent winners excluded from the next spin under these options.</summary>
        public int ExcludedWinnerCount
        {
            get
            {
                if (!Enabled)
                {
                    return 0;
                }

                switch (Exclusion)
                {
                    case RerollExclusionMode.PreviousWinner: return 1;
                    case RerollExclusionMode.RecentWinners: return RecentWinnerCount;
                    default: return 0;
                }
            }
        }

        public RerollProtectionOptions Clone() => (RerollProtectionOptions)MemberwiseClone();

        /// <summary>Repairs out-of-range values (hand edits, older versions).</summary>
        public RerollProtectionOptions Sanitize()
        {
            Exclusion = Enum.IsDefined(typeof(RerollExclusionMode), Exclusion) ? Exclusion : RerollExclusionMode.PreviousWinner;
            ResetBehaviour = Enum.IsDefined(typeof(RerollResetBehaviour), ResetBehaviour) ? ResetBehaviour : RerollResetBehaviour.ResetOnRestart;
            RecentWinnerCount = Clamp(RecentWinnerCount, MinCount, MaxCount);
            MaxRerolls = Clamp(MaxRerolls, MinCount, MaxCount);
            CooldownMinutes = Clamp(CooldownMinutes, MinCooldownMinutes, MaxCooldownMinutes);
            return this;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    /// <summary>Per-wheel protection state. Only ids, a counter and a timestamp are persisted.</summary>
    public sealed class RerollState
    {
        /// <summary>Most recent first, at most <see cref="RerollProtectionOptions.MaxCount"/> entries.</summary>
        public List<Guid> RecentWinnerIds { get; set; } = new List<Guid>();

        /// <summary>Rerolls used in the current selection session.</summary>
        public int RerollCount { get; set; }

        public DateTime? CooldownUntilUtc { get; set; }

        /// <summary>Set while a pick is waiting to be accepted or rerolled. Null between sessions.</summary>
        public Guid? SessionId { get; set; }

        public bool IsEmpty => (RecentWinnerIds == null || RecentWinnerIds.Count == 0)
            && RerollCount == 0 && !CooldownUntilUtc.HasValue && !SessionId.HasValue;

        public RerollState Sanitize()
        {
            RecentWinnerIds = (RecentWinnerIds ?? new List<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Take(RerollProtectionOptions.MaxCount)
                .ToList();
            RerollCount = Math.Max(0, Math.Min(RerollProtectionOptions.MaxCount, RerollCount));
            if (CooldownUntilUtc.HasValue)
            {
                var value = CooldownUntilUtc.Value;
                CooldownUntilUtc = value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : value.ToUniversalTime();
            }

            if (SessionId == Guid.Empty)
            {
                SessionId = null;
            }

            if (!SessionId.HasValue)
            {
                // A count or cooldown without a session is meaningless.
                RerollCount = 0;
                CooldownUntilUtc = null;
            }

            return this;
        }
    }

    public enum SpinBlockReason
    {
        None,
        EmptyWheel,
        AllCandidatesExcluded,
        RerollLimitReached,
        CoolingDown
    }

    /// <summary>
    /// The explicit outcome of the eligibility stage: who can win the next spin, who was left out and why.
    /// The winner is always drawn uniformly from <see cref="EligibleIndices"/>.
    /// </summary>
    public sealed class SelectionResult
    {
        private static readonly int[] NoIndices = new int[0];
        private static readonly Guid[] NoIds = new Guid[0];

        /// <summary>Every game shown on the wheel, in wheel order.</summary>
        public IReadOnlyList<GameInfo> Entries { get; internal set; } = new GameInfo[0];

        /// <summary>Indices into <see cref="Entries"/> that can win. Empty when blocked.</summary>
        public IReadOnlyList<int> EligibleIndices { get; internal set; } = NoIndices;

        /// <summary>Games left out of this spin by recent-winner protection.</summary>
        public IReadOnlyList<Guid> ExcludedIds { get; internal set; } = NoIds;

        public SpinBlockReason BlockReason { get; internal set; }

        public bool IsBlocked => BlockReason != SpinBlockReason.None;

        /// <summary>User-facing explanation when blocked, or a note when protection was bypassed.</summary>
        public string Message { get; internal set; }

        public bool ProtectionEnabled { get; internal set; }

        /// <summary>True when a single-game wheel was allowed to spin despite its game being a recent winner.</summary>
        public bool ExclusionBypassed { get; internal set; }

        /// <summary>Rerolls left in this session, or null when rerolls are unlimited.</summary>
        public int? RerollsRemaining { get; internal set; }

        public int? MaxRerolls { get; internal set; }

        public DateTime? CooldownUntilUtc { get; internal set; }

        /// <summary>True while a pick is waiting to be accepted, so the next spin counts as a reroll.</summary>
        public bool SessionActive { get; internal set; }

        public int EligibleCount => EligibleIndices.Count;
    }
}
