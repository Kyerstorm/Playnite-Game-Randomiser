namespace GameRandomiser.Core.Models
{
    /// <summary>How entries are physically arranged around a wheel. Never affects probability.</summary>
    public enum SortMode
    {
        Alphabetical = 0,
        Random = 1,
        Library = 2
    }

    /// <summary>How each game is drawn inside its wheel segment.</summary>
    public enum GameDisplayMode
    {
        Text = 0,
        Cover = 1,
        SmallCoverAndTitle = 2,
        IconAndTitle = 3
    }

    /// <summary>What happens to the winning entry once the wheel stops.</summary>
    public enum WinnerBehaviour
    {
        KeepGame = 0,
        AskMe = 1,
        RemoveAutomatically = 2
    }

    public enum ThemeMode
    {
        Native = 0,
        Light = 1,
        Dark = 2
    }

    public enum WinnerPresentation
    {
        Card = 0,
        Compact = 1
    }

    /// <summary>
    /// How a criteria-generated wheel keeps its game list. Values are persisted; never renumber.
    /// Wheels without criteria are always <see cref="ManualSnapshot"/>.
    /// </summary>
    public enum MembershipPolicy
    {
        /// <summary>The list only changes when the user edits it or explicitly refreshes.</summary>
        ManualSnapshot = 0,

        /// <summary>Only games currently matching the criteria are on the wheel.</summary>
        StrictCriteria = 1,

        /// <summary>Games matching the criteria, plus games the user pinned.</summary>
        CriteriaPlusPinned = 2
    }

    /// <summary>Where a dynamic wheel's membership stands relative to the library. Computed, never persisted.</summary>
    public enum RefreshStatus
    {
        /// <summary>The wheel is not kept in sync automatically.</summary>
        NotApplicable = 0,

        /// <summary>Dynamic, but not refreshed yet.</summary>
        Ready = 1,
        Refreshing = 2,
        Refreshed = 3,

        /// <summary>The refresh failed and the wheel has never refreshed successfully.</summary>
        Failed = 4,

        /// <summary>The refresh failed; the list shown is the last successful one and may be stale.</summary>
        FailedUsingLastKnownGood = 5
    }

    /// <summary>Which recent winners are left out of the next spin. Values are persisted; never renumber.</summary>
    public enum RerollExclusionMode
    {
        None = 0,
        PreviousWinner = 1,
        RecentWinners = 2
    }

    public enum RerollResetBehaviour
    {
        /// <summary>Protection state is forgotten when Playnite restarts.</summary>
        ResetOnRestart = 0,

        /// <summary>Protection state survives restarts until it is reset or a pick is accepted.</summary>
        KeepUntilReset = 1
    }

    /// <summary>
    /// Aspects of the library a population rule reads. Library events report which aspects changed,
    /// so only the wheels whose rules depend on them are refreshed.
    /// </summary>
    [System.Flags]
    public enum LibraryFields
    {
        None = 0,

        /// <summary>Games were added to or removed from the library. Every rule depends on this.</summary>
        Collection = 1,

        /// <summary>A game was hidden or unhidden. Every rule depends on this.</summary>
        Hidden = 2,
        Installed = 4,
        Playtime = 8,

        /// <summary>Last played date or play count.</summary>
        Activity = 16,
        Genres = 32,
        Platforms = 64,
        Tags = 128,
        Categories = 256,
        CompletionStatus = 512,

        /// <summary>Playnite's current library filter, which changes without raising library events.</summary>
        ViewFilter = 1024,

        All = Collection | Hidden | Installed | Playtime | Activity | Genres | Platforms | Tags | Categories | CompletionStatus | ViewFilter
    }

    /// <summary>How aggressively the wheel spins (full rotations per second of spin duration).</summary>
    public enum SpinIntensity
    {
        Gentle = 0,
        Normal = 1,
        Wild = 2
    }
}
