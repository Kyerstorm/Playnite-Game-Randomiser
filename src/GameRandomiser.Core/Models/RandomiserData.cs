using System;
using System.Collections.Generic;

namespace GameRandomiser.Core.Models
{
    /// <summary>
    /// Root persisted document. Global settings live in Playnite's own plugin settings
    /// (see RandomiserSettings in the plugin project); this document holds user data only.
    /// </summary>
    public class RandomiserData
    {
        /// <summary>Bump when the persisted shape changes and add a step to DataMigrator.</summary>
        public const int CurrentSchemaVersion = 1;

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

        /// <summary>Playnite game ids in physical wheel order. Metadata is always resolved live.</summary>
        public List<Guid> GameIds { get; set; } = new List<Guid>();

        public SortMode SortMode { get; set; } = SortMode.Alphabetical;

        public List<SpinHistoryEntry> History { get; set; } = new List<SpinHistoryEntry>();

        /// <summary>Criteria the wheel was created from, if automatically populated.</summary>
        public PopulationSpec Population { get; set; }

        /// <summary>
        /// Reserved for future dynamic wheels that re-evaluate <see cref="Population"/> on library changes.
        /// Currently always false: automatic wheels are snapshots.
        /// </summary>
        public bool IsDynamic { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
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
