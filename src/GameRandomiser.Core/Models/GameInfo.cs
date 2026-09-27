using System;
using System.Collections.Generic;

namespace GameRandomiser.Core.Models
{
    /// <summary>
    /// A read-only, live-resolved view of a Playnite game. Never persisted:
    /// wheels only store ids and resolve this on demand so names, art and install state never go stale.
    /// </summary>
    public sealed class GameInfo
    {
        private static readonly IReadOnlyList<Guid> Empty = new Guid[0];

        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SortingName { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsHidden { get; set; }

        /// <summary>Absolute path to cover art, or null if none.</summary>
        public string CoverPath { get; set; }

        /// <summary>Absolute path to icon, or null if none.</summary>
        public string IconPath { get; set; }

        public ulong PlaytimeSeconds { get; set; }
        public ulong PlayCount { get; set; }
        public DateTime? LastActivity { get; set; }
        public DateTime? Added { get; set; }
        public Guid CompletionStatusId { get; set; }

        public IReadOnlyList<Guid> GenreIds { get; set; } = Empty;
        public IReadOnlyList<Guid> PlatformIds { get; set; } = Empty;
        public IReadOnlyList<Guid> TagIds { get; set; } = Empty;
        public IReadOnlyList<Guid> CategoryIds { get; set; } = Empty;

        /// <summary>Position of the game in Playnite's library ordering (used by SortMode.Library).</summary>
        public int LibraryIndex { get; set; }

        public string SortKey => string.IsNullOrWhiteSpace(SortingName) ? Name ?? string.Empty : SortingName;

        public bool HasBeenPlayed => PlaytimeSeconds > 0 || PlayCount > 0 || LastActivity.HasValue;

        public override string ToString() => Name;
    }

    /// <summary>A Playnite lookup item (genre, platform, tag, ...) offered as population criteria.</summary>
    public sealed class NamedItem
    {
        public NamedItem(Guid id, string name)
        {
            Id = id;
            Name = name ?? string.Empty;
        }

        public Guid Id { get; }
        public string Name { get; }

        public override string ToString() => Name;
    }

    public enum LookupKind
    {
        Genre,
        Platform,
        Tag,
        Category,
        CompletionStatus
    }
}
