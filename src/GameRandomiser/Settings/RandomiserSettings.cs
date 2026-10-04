using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Layout;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Settings
{
    /// <summary>
    /// Global extension configuration, persisted by Playnite's plugin settings system (config.json).
    /// Wheels and history are stored separately in data.json by the Core JsonFileStore.
    /// </summary>
    public class RandomiserSettings
    {
        public const int CurrentVersion = 2;

        public int SettingsVersion { get; set; } = CurrentVersion;

        // Appearance
        public ThemeMode Theme { get; set; } = ThemeMode.Native;

        /// <summary>Hex colour, or empty to use the theme's accent (Playnite glyph colour in Native mode).</summary>
        public string AccentColour { get; set; } = string.Empty;

        public List<string> SegmentColours { get; set; } = SegmentPalette.DefaultColours.ToList();

        public bool ShowSegmentBorders { get; set; } = true;

        // Wheel
        /// <summary>Wheel selected when Playnite starts. Null means "whichever wheel was used last".</summary>
        public Guid? DefaultWheelId { get; set; }

        public GameDisplayMode DisplayMode { get; set; } = GameDisplayMode.Text;

        public SortMode DefaultSortMode { get; set; } = SortMode.Alphabetical;

        public bool ShowRecentPicksWhenWide { get; set; } = true;

        // Animation
        public double SpinDurationSeconds { get; set; } = 5.0;

        public SpinIntensity SpinIntensity { get; set; } = SpinIntensity.Normal;

        // Sound
        public bool TickSoundEnabled { get; set; } = true;

        public bool WinnerSoundEnabled { get; set; } = true;

        /// <summary>0..100</summary>
        public int Volume { get; set; } = 50;

        // Winner
        public WinnerPresentation WinnerPresentation { get; set; } = WinnerPresentation.Card;

        public WinnerBehaviour WinnerBehaviour { get; set; } = WinnerBehaviour.AskMe;

        /// <summary>Off by default: the user stays in control of what happens after a spin.</summary>
        public bool AutoLaunchWinner { get; set; }

        public bool ShowConfetti { get; set; } = true;

        // Reroll protection (off by default; state is kept per wheel in data.json)
        public RerollProtectionOptions Reroll { get; set; } = new RerollProtectionOptions();

        public RandomiserSettings Clone()
        {
            var clone = (RandomiserSettings)MemberwiseClone();
            clone.SegmentColours = new List<string>(SegmentColours ?? new List<string>());
            clone.Reroll = (Reroll ?? new RerollProtectionOptions()).Clone();
            return clone;
        }

        /// <summary>Repairs out-of-range or missing values (hand edits, older versions).</summary>
        public RandomiserSettings Sanitize()
        {
            SegmentColours = (SegmentColours ?? new List<string>())
                .Where(c => SegmentPalette.TryParseHex(c, out _, out _, out _))
                .ToList();
            if (SegmentColours.Count == 0)
            {
                SegmentColours = SegmentPalette.DefaultColours.ToList();
            }

            if (!string.IsNullOrWhiteSpace(AccentColour) && !SegmentPalette.TryParseHex(AccentColour, out _, out _, out _))
            {
                AccentColour = string.Empty;
            }

            if (double.IsNaN(SpinDurationSeconds))
            {
                SpinDurationSeconds = 5;
            }

            SpinDurationSeconds = Math.Max(SpinOptions.MinDurationSeconds, Math.Min(SpinOptions.MaxDurationSeconds, SpinDurationSeconds));
            Volume = Math.Max(0, Math.Min(100, Volume));
            Theme = Enum.IsDefined(typeof(ThemeMode), Theme) ? Theme : ThemeMode.Native;
            DisplayMode = Enum.IsDefined(typeof(GameDisplayMode), DisplayMode) ? DisplayMode : GameDisplayMode.Text;
            DefaultSortMode = Enum.IsDefined(typeof(SortMode), DefaultSortMode) ? DefaultSortMode : SortMode.Alphabetical;
            SpinIntensity = Enum.IsDefined(typeof(SpinIntensity), SpinIntensity) ? SpinIntensity : SpinIntensity.Normal;
            WinnerPresentation = Enum.IsDefined(typeof(WinnerPresentation), WinnerPresentation) ? WinnerPresentation : WinnerPresentation.Card;
            WinnerBehaviour = Enum.IsDefined(typeof(WinnerBehaviour), WinnerBehaviour) ? WinnerBehaviour : WinnerBehaviour.AskMe;
            Reroll = (Reroll ?? new RerollProtectionOptions()).Sanitize();
            SettingsVersion = CurrentVersion;
            return this;
        }

        public SpinOptions ToSpinOptions() => new SpinOptions
        {
            DurationSeconds = SpinDurationSeconds,
            Intensity = SpinIntensity
        };
    }
}
