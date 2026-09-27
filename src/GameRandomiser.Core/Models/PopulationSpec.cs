using System;
using System.Collections.Generic;

namespace GameRandomiser.Core.Models
{
    /// <summary>Identifies an automatic population rule. Values are persisted; never renumber.</summary>
    public enum PopulationSource
    {
        AllGames = 0,
        FilteredLibrary = 1,
        Genre = 2,
        Platform = 3,
        Tag = 4,
        Category = 5,
        CompletionStatus = 6,
        NeverPlayed = 7,
        Played = 8,
        PlaytimeLessThan = 9,
        PlaytimeMoreThan = 10,
        RecentlyPlayed = 11,
        Installed = 12,
        NotInstalled = 13
    }

    /// <summary>Serializable description of the criteria used to populate a wheel.</summary>
    public class PopulationSpec
    {
        public PopulationSource Source { get; set; }

        /// <summary>Lookup item ids (genre/platform/tag/category/status). A game matches if it has any.</summary>
        public List<Guid> ItemIds { get; set; } = new List<Guid>();

        /// <summary>Hours threshold for playtime rules, or days window for recently played.</summary>
        public double? Amount { get; set; }

        /// <summary>Human readable summary captured at creation time.</summary>
        public string Description { get; set; }

        public PopulationSpec Clone()
        {
            return new PopulationSpec
            {
                Source = Source,
                ItemIds = new List<Guid>(ItemIds ?? new List<Guid>()),
                Amount = Amount,
                Description = Description
            };
        }
    }
}
