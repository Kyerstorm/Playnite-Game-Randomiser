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

    /// <summary>How aggressively the wheel spins (full rotations per second of spin duration).</summary>
    public enum SpinIntensity
    {
        Gentle = 0,
        Normal = 1,
        Wild = 2
    }
}
