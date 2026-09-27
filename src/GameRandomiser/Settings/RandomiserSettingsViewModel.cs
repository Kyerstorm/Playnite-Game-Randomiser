using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GameRandomiser.Core.Layout;
using GameRandomiser.Core.Models;
using GameRandomiser.UI;
using Playnite.SDK;

namespace GameRandomiser.Settings
{
    public sealed class ColourEntry : BindableBase
    {
        private string hex;

        public ColourEntry(string hex) => this.hex = hex;

        public string Hex { get => hex; set => Set(ref hex, value); }
    }

    /// <summary>Playnite settings contract (BeginEdit / CancelEdit / EndEdit / VerifySettings) for the extension.</summary>
    public sealed class RandomiserSettingsViewModel : BindableBase, ISettings
    {
        private readonly GameRandomiserPlugin plugin;
        private RandomiserSettings settings;
        private RandomiserSettings editingClone;
        private List<Option<Guid>> defaultWheelOptions = new List<Option<Guid>>();

        public RandomiserSettingsViewModel(GameRandomiserPlugin plugin)
        {
            this.plugin = plugin;
            RandomiserSettings saved = null;
            try
            {
                saved = plugin.LoadPluginSettings<RandomiserSettings>();
            }
            catch (Exception e)
            {
                LogManager.GetLogger().Error(e, "Game Randomiser settings were unreadable; using defaults.");
            }

            settings = (saved ?? new RandomiserSettings()).Sanitize();

            AddColourCommand = new DelegateCommand(_ => SegmentColours.Add(new ColourEntry(NextSuggestedColour())));
            RemoveColourCommand = new DelegateCommand(p =>
            {
                if (p is ColourEntry entry && SegmentColours.Count > 1)
                {
                    SegmentColours.Remove(entry);
                }
            });
            ResetColoursCommand = new DelegateCommand(_ => ResetColours(SegmentPalette.DefaultColours));
            ResetAccentCommand = new DelegateCommand(_ =>
            {
                Settings.AccentColour = string.Empty;
                OnPropertyChanged(nameof(AccentColour));
            });
            ResetColours(settings.SegmentColours);
        }

        public RandomiserSettings Settings
        {
            get => settings;
            private set => Set(ref settings, value);
        }

        public ObservableCollection<ColourEntry> SegmentColours { get; } = new ObservableCollection<ColourEntry>();

        /// <summary>Accent hex, surfaced separately so the colour picker can be reset to "theme default".</summary>
        public string AccentColour
        {
            get => Settings.AccentColour;
            set
            {
                Settings.AccentColour = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public double SpinDurationSeconds
        {
            get => Settings.SpinDurationSeconds;
            set
            {
                Settings.SpinDurationSeconds = Math.Round(value * 2) / 2; // half-second steps
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpinDurationText));
            }
        }

        public string SpinDurationText => $"{Settings.SpinDurationSeconds:0.#} seconds";

        public int Volume
        {
            get => Settings.Volume;
            set
            {
                Settings.Volume = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumeText));
            }
        }

        public string VolumeText => Settings.Volume + "%";

        public DelegateCommand AddColourCommand { get; }
        public DelegateCommand RemoveColourCommand { get; }
        public DelegateCommand ResetColoursCommand { get; }
        public DelegateCommand ResetAccentCommand { get; }

        public IReadOnlyList<Option<ThemeMode>> ThemeOptions { get; } = new[]
        {
            new Option<ThemeMode>(ThemeMode.Native, "Match Playnite theme"),
            new Option<ThemeMode>(ThemeMode.Dark, "Dark"),
            new Option<ThemeMode>(ThemeMode.Light, "Light")
        };

        public IReadOnlyList<Option<GameDisplayMode>> DisplayModeOptions { get; } = new[]
        {
            new Option<GameDisplayMode>(GameDisplayMode.Text, "Title"),
            new Option<GameDisplayMode>(GameDisplayMode.Cover, "Game cover"),
            new Option<GameDisplayMode>(GameDisplayMode.SmallCoverAndTitle, "Small cover + title"),
            new Option<GameDisplayMode>(GameDisplayMode.IconAndTitle, "Icon + title")
        };

        public IReadOnlyList<Option<SortMode>> SortOptions { get; } = new[]
        {
            new Option<SortMode>(SortMode.Alphabetical, "A–Z"),
            new Option<SortMode>(SortMode.Random, "Random"),
            new Option<SortMode>(SortMode.Library, "Library order")
        };

        public IReadOnlyList<Option<SpinIntensity>> IntensityOptions { get; } = new[]
        {
            new Option<SpinIntensity>(SpinIntensity.Gentle, "Gentle"),
            new Option<SpinIntensity>(SpinIntensity.Normal, "Normal"),
            new Option<SpinIntensity>(SpinIntensity.Wild, "Wild")
        };

        public IReadOnlyList<Option<WinnerPresentation>> PresentationOptions { get; } = new[]
        {
            new Option<WinnerPresentation>(WinnerPresentation.Card, "Winner card with artwork"),
            new Option<WinnerPresentation>(WinnerPresentation.Compact, "Compact banner")
        };

        public IReadOnlyList<Option<WinnerBehaviour>> BehaviourOptions { get; } = new[]
        {
            new Option<WinnerBehaviour>(WinnerBehaviour.KeepGame, "Keep game on the wheel"),
            new Option<WinnerBehaviour>(WinnerBehaviour.AskMe, "Ask me (default)"),
            new Option<WinnerBehaviour>(WinnerBehaviour.RemoveAutomatically, "Remove automatically")
        };

        /// <summary>Guid.Empty stands for "last used wheel" (WPF combo boxes can't select a null value).</summary>
        public List<Option<Guid>> DefaultWheelOptions
        {
            get => defaultWheelOptions;
            private set => Set(ref defaultWheelOptions, value);
        }

        public Guid DefaultWheelId
        {
            get => Settings.DefaultWheelId ?? Guid.Empty;
            set
            {
                Settings.DefaultWheelId = value == Guid.Empty ? (Guid?)null : value;
                OnPropertyChanged();
            }
        }

        public void BeginEdit()
        {
            editingClone = Settings.Clone();
            ResetColours(Settings.SegmentColours);
            var options = new List<Option<Guid>> { new Option<Guid>(Guid.Empty, "Last used wheel") };
            if (plugin.Context != null)
            {
                options.AddRange(plugin.Context.Wheels.Wheels.Select(w => new Option<Guid>(w.Id, $"{w.Icon}  {w.Name}")));
            }

            DefaultWheelOptions = options;
            if (Settings.DefaultWheelId.HasValue && options.All(o => o.Value != Settings.DefaultWheelId.Value))
            {
                Settings.DefaultWheelId = null;
            }

            RaiseAll();
        }

        public void CancelEdit()
        {
            if (editingClone != null)
            {
                Settings = editingClone;
                ResetColours(Settings.SegmentColours);
                RaiseAll();
            }
        }

        public void EndEdit()
        {
            Settings.SegmentColours = SegmentColours.Select(c => c.Hex).ToList();
            Settings.Sanitize();
            plugin.SavePluginSettings(Settings);
            plugin.Context?.OnSettingsSaved();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (SegmentColours.Count == 0)
            {
                errors.Add("Add at least one segment colour.");
            }

            foreach (var colour in SegmentColours)
            {
                if (!SegmentPalette.TryParseHex(colour.Hex, out _, out _, out _))
                {
                    errors.Add($"\"{colour.Hex}\" is not a valid colour. Use a hex value like #3369E8.");
                }
            }

            if (!string.IsNullOrWhiteSpace(Settings.AccentColour) && !SegmentPalette.TryParseHex(Settings.AccentColour, out _, out _, out _))
            {
                errors.Add($"\"{Settings.AccentColour}\" is not a valid accent colour.");
            }

            return errors.Count == 0;
        }

        private void ResetColours(IEnumerable<string> colours)
        {
            SegmentColours.Clear();
            foreach (var c in colours)
            {
                SegmentColours.Add(new ColourEntry(c));
            }
        }

        private string NextSuggestedColour()
        {
            var used = new HashSet<string>(SegmentColours.Select(c => c.Hex), StringComparer.OrdinalIgnoreCase);
            return SegmentPalette.DefaultColours.FirstOrDefault(c => !used.Contains(c)) ?? "#607D8B";
        }

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(DefaultWheelId));
            OnPropertyChanged(nameof(AccentColour));
            OnPropertyChanged(nameof(SpinDurationSeconds));
            OnPropertyChanged(nameof(SpinDurationText));
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumeText));
        }
    }
}
