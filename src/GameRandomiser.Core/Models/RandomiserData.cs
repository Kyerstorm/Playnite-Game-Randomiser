using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GameRandomiser.Core.Models
{
    /// <summary>
    /// Root persisted document. Global settings live in Playnite's own plugin settings
    /// (see RandomiserSettings in the plugin project); this document holds user data only.
    /// </summary>
    public class RandomiserData
    {
        /// <summary>Bump when the persisted shape changes and add a step to DataMigrator.</summary>
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<RandomiserWheel> Wheels { get; set; } = new List<RandomiserWheel>();

        public Guid? ActiveWheelId { get; set; }
    }

    public class RandomiserWheel
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        /// <summary>Short glyph shown beside the wheel name (e.g. an emoji).</summary>
        public string Icon { get; set; } = "🎲";

        /// <summary>
        /// Current membership: Playnite game ids in physical wheel order. Metadata is always resolved live.
        /// For dynamic wheels this is the last successfully reconciled list.
        /// </summary>
        public List<Guid> GameIds { get; set; } = new List<Guid>();

        public SortMode SortMode { get; set; } = SortMode.Alphabetical;

        public List<SpinHistoryEntry> History { get; set; } = new List<SpinHistoryEntry>();

        /// <summary>Population rules: the criteria the wheel was created from, if automatically populated.</summary>
        public PopulationSpec Population { get; set; }

        /// <summary>How <see cref="Population"/> is applied over time. Existing wheels stay snapshots.</summary>
        public MembershipPolicy MembershipPolicy { get; set; } = MembershipPolicy.ManualSnapshot;

        /// <summary>Games kept on a <see cref="MembershipPolicy.CriteriaPlusPinned"/> wheel even when they stop matching.</summary>
        public List<Guid> PinnedGameIds { get; set; } = new List<Guid>();

        /// <summary>Games the user removed from a dynamic wheel; they stay off it even while they match.</summary>
        public List<Guid> ExcludedGameIds { get; set; } = new List<Guid>();

        /// <summary>When a dynamic wheel last reconciled successfully.</summary>
        public DateTime? LastRefreshUtc { get; set; }

        /// <summary>User-facing reason the most recent refresh failed, or null if it succeeded.</summary>
        public string RefreshError { get; set; }

        /// <summary>Reroll protection state, or null when there is none.</summary>
        public RerollState Reroll { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>True when the wheel re-evaluates <see cref="Population"/> as the library changes.</summary>
        [JsonIgnore]
        public bool IsDynamic => Population != null && MembershipPolicy != MembershipPolicy.ManualSnapshot;

        /// <summary>
        /// In-memory change counter for anything a refresh reads. A refresh computed against an older
        /// revision is discarded rather than applied over the user's newer edits.
        /// </summary>
        [JsonIgnore]
        internal int Revision { get; set; }
    }

    public class SpinHistoryEntry
    {
        public Guid GameId { get; set; }

        /// <summary>
        /// Name at spin time. Only used as a fallback label if the game later leaves the library,
        /// so history stays readable; live data is preferred whenever the game still exists.
        /// </summary>
        public string GameName { get; set; }

        public DateTime SpunAtUtc { get; set; }
    }
}
