using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Persistence;
using GameRandomiser.Core.Population;
using GameRandomiser.Core.Services;
using GameRandomiser.Integration;
using GameRandomiser.Settings;
using GameRandomiser.UI;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRandomiser.Services
{
    /// <summary>
    /// Composition root: owns every service for the lifetime of the plugin and bridges Playnite
    /// library events into the Core services. Views receive this rather than the raw Playnite API.
    /// </summary>
    public sealed class RandomiserContext : IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly RandomiserSettingsViewModel settingsViewModel;
        private readonly CryptoRandomSource random = new CryptoRandomSource();
        private DispatcherTimer libraryDebounce;
        private bool initialized;

        public RandomiserContext(GameRandomiserPlugin plugin, IPlayniteAPI api, RandomiserSettingsViewModel settingsViewModel)
        {
            Plugin = plugin;
            Api = api;
            this.settingsViewModel = settingsViewModel;

            Catalog = new PlayniteGameCatalog(api);
            var store = new ResilientStore(new JsonFileStore(Path.Combine(plugin.GetPluginUserDataPath(), "data.json")), api);
            Wheels = new WheelService(store, Catalog, random);
            History = new HistoryService(Wheels);
            Randomiser = new RandomiserService(random);
            Population = new PopulationEngine(Catalog);
            Actions = new PlayniteGameActions(api, Catalog);
            Audio = new AudioService();
            Images = new ImageCache();
            ApplyAudioSettings();
        }

        public GameRandomiserPlugin Plugin { get; }
        public IPlayniteAPI Api { get; }
        public PlayniteGameCatalog Catalog { get; }
        public WheelService Wheels { get; }
        public HistoryService History { get; }
        public RandomiserService Randomiser { get; }
        public PopulationEngine Population { get; }
        public PlayniteGameActions Actions { get; }
        public AudioService Audio { get; }
        public ImageCache Images { get; }

        public RandomiserSettings Settings => settingsViewModel.Settings;

        public Dispatcher Dispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        /// <summary>Global settings were saved.</summary>
        public event EventHandler SettingsChanged;

        /// <summary>Game metadata changed (names, art, install state). Debounced.</summary>
        public event EventHandler LibraryChanged;

        /// <summary>Called once Playnite's database is open.</summary>
        public void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            libraryDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            libraryDebounce.Tick += (s, e) =>
            {
                libraryDebounce.Stop();
                Catalog.InvalidateLibraryOrder();
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            };

            Api.Database.Games.ItemCollectionChanged += OnGamesCollectionChanged;
            Api.Database.Games.ItemUpdated += OnGamesUpdated;

            SafeRun("clean up wheels", () =>
            {
                var removed = Wheels.CleanupInvalidEntries();
                if (removed > 0)
                {
                    Logger.Info($"Game Randomiser removed {removed} deleted or hidden games from wheels.");
                }
            });

            var defaultWheel = Settings.DefaultWheelId;
            if (defaultWheel.HasValue && Wheels.GetWheel(defaultWheel.Value) != null)
            {
                SafeRun("select default wheel", () => Wheels.SetActiveWheel(defaultWheel.Value));
            }

            if (!string.IsNullOrEmpty(Wheels.LoadWarning))
            {
                Api.Notifications.Add(new NotificationMessage("GameRandomiser_LoadWarning", Wheels.LoadWarning, NotificationType.Error));
            }
        }

        internal void OnSettingsSaved()
        {
            ApplyAudioSettings();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyAudioSettings()
        {
            Audio.TickEnabled = Settings.TickSoundEnabled;
            Audio.WinnerEnabled = Settings.WinnerSoundEnabled;
            Audio.Volume = Settings.Volume;
        }

        private void OnGamesCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            var removed = e.RemovedItems?.Select(g => g.Id).ToList() ?? new List<Guid>();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (removed.Count > 0)
                {
                    SafeRun("remove deleted games", () => Wheels.RemoveGamesFromAllWheels(removed));
                }

                ScheduleLibraryRefresh();
            }));
        }

        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            var nowHidden = new List<Guid>();
            var relevant = false;
            foreach (var update in e.UpdatedItems ?? new List<ItemUpdateEvent<Game>>())
            {
                var oldData = update.OldData;
                var newData = update.NewData;
                if (newData == null)
                {
                    continue;
                }

                if (newData.Hidden && (oldData == null || !oldData.Hidden))
                {
                    nowHidden.Add(newData.Id);
                }

                if (oldData == null
                    || oldData.Name != newData.Name
                    || oldData.SortingName != newData.SortingName
                    || oldData.CoverImage != newData.CoverImage
                    || oldData.Icon != newData.Icon
                    || oldData.IsInstalled != newData.IsInstalled
                    || oldData.Hidden != newData.Hidden)
                {
                    relevant = true;
                }
            }

            if (nowHidden.Count == 0 && !relevant)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (nowHidden.Count > 0)
                {
                    // Keep wheel data clean: hidden games are never eligible.
                    SafeRun("remove hidden games", () => Wheels.RemoveGamesFromAllWheels(nowHidden));
                }

                ScheduleLibraryRefresh();
            }));
        }

        private void ScheduleLibraryRefresh()
        {
            if (libraryDebounce == null)
            {
                return;
            }

            libraryDebounce.Stop();
            libraryDebounce.Start();
        }

        // ---- Shared interactive flows (used by the sidebar, context menu and main menu) ----

        /// <summary>Opens the randomiser as a standalone window (works from menus, and in any view).</summary>
        public void OpenWindow(SidebarTab tab = SidebarTab.Wheel)
        {
            SafeRun("open the randomiser", () =>
            {
                var window = Api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = true,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true
                });
                var view = new SidebarView(this);
                view.SelectTab(tab);
                window.Title = "Game Randomiser";
                window.Width = 960;
                window.Height = 800;
                window.MinWidth = 380;
                window.MinHeight = 480;
                window.Content = view;
                window.Owner = Api.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Closed += (s, e) => view.Detach();
                window.Show();
            });
        }

        public RandomiserWheel CreateWheelInteractive(IEnumerable<Guid> preselectedGames = null)
        {
            RandomiserWheel created = null;
            SafeRun("create a wheel", () => created = WheelCreationWindow.ShowDialog(this, preselectedGames?.ToList()));
            return created;
        }

        /// <summary>Lets the user pick a wheel. Returns null if cancelled.</summary>
        public RandomiserWheel ChooseWheel(string caption, IList<Guid> gamesForNewWheel)
        {
            RandomiserWheel chosen = null;
            SafeRun("choose a wheel", () =>
            {
                const string CreateLabel = "➕  Create new wheel…";
                List<GenericItemOption> Build(string search)
                {
                    var items = Wheels.Wheels
                        .Where(w => string.IsNullOrWhiteSpace(search) || w.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        .Select(w => (GenericItemOption)new WheelOption(w))
                        .ToList();
                    items.Add(new GenericItemOption(CreateLabel, "Create a new wheel containing the selected games"));
                    return items;
                }

                var result = Api.Dialogs.ChooseItemWithSearch(Build(null), Build, null, caption);
                if (result is WheelOption option)
                {
                    chosen = Wheels.GetWheel(option.WheelId);
                }
                else if (result != null && result.Name == CreateLabel)
                {
                    chosen = CreateWheelInteractive(gamesForNewWheel);
                    if (chosen != null)
                    {
                        // Games were added during creation; signal to the caller that nothing more is needed.
                        chosen = null;
                    }
                }
            });
            return chosen;
        }

        public void AddGamesWithFeedback(RandomiserWheel wheel, IList<Guid> gameIds)
        {
            if (wheel == null || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            SafeRun("add games", () =>
            {
                var result = Wheels.AddGames(wheel.Id, gameIds);
                if (result.Added == 0 && result.AlreadyPresent > 0 && result.Ineligible == 0)
                {
                    var message = gameIds.Count == 1
                        ? $"\"{Catalog.TryGet(gameIds[0])?.Name}\" is already on the {wheel.Name} wheel."
                        : $"All {gameIds.Count} games are already on the {wheel.Name} wheel.";
                    Api.Dialogs.ShowMessage(message, "Game Randomiser");
                }
                else if (result.AlreadyPresent > 0 || result.Ineligible > 0)
                {
                    var parts = new List<string> { $"Added {Plural(result.Added, "game")} to {wheel.Name}." };
                    if (result.AlreadyPresent > 0)
                    {
                        parts.Add($"{Plural(result.AlreadyPresent, "game was", "games were")} already on the wheel.");
                    }

                    if (result.Ineligible > 0)
                    {
                        parts.Add($"{Plural(result.Ineligible, "hidden or unavailable game was", "hidden or unavailable games were")} skipped.");
                    }

                    Api.Dialogs.ShowMessage(string.Join(" ", parts), "Game Randomiser");
                }
            });
        }

        /// <summary>Shows the game picker for a wheel and adds the chosen games.</summary>
        public void AddGamesInteractive(RandomiserWheel wheel)
        {
            if (wheel == null)
            {
                return;
            }

            SafeRun("add games", () =>
            {
                var picked = GamePickerWindow.ShowDialog(this, $"Add games to {wheel.Name}", new HashSet<Guid>(wheel.GameIds));
                if (picked != null && picked.Count > 0)
                {
                    AddGamesWithFeedback(wheel, picked);
                }
            });
        }

        public void OpenSettings() => SafeRun("open settings", () => Plugin.OpenSettingsView());

        /// <summary>Runs an action, logging and reporting any failure instead of letting it reach Playnite.</summary>
        public void SafeRun(string what, Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException e)
            {
                // Validation messages from Core are written for users.
                Api.Dialogs.ShowMessage(e.Message, "Game Randomiser");
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Game Randomiser failed to {what}.");
                Api.Dialogs.ShowErrorMessage($"Game Randomiser couldn't {what}.\n\n{e.Message}", "Game Randomiser");
            }
        }

        public static string Plural(int count, string singular, string plural = null) =>
            count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

        public void Dispose()
        {
            try
            {
                if (initialized)
                {
                    Api.Database.Games.ItemCollectionChanged -= OnGamesCollectionChanged;
                    Api.Database.Games.ItemUpdated -= OnGamesUpdated;
                }
            }
            catch (Exception)
            {
                // Database may already be closed during shutdown.
            }

            libraryDebounce?.Stop();
            Audio.Dispose();
            random.Dispose();
        }

        private sealed class WheelOption : GenericItemOption
        {
            public WheelOption(RandomiserWheel wheel)
                : base($"{wheel.Icon}  {wheel.Name}", Plural(wheel.GameIds.Count, "game"))
            {
                WheelId = wheel.Id;
            }

            public Guid WheelId { get; }
        }

        /// <summary>Never lets a disk error escape into the UI; the data stays in memory and is retried on the next change.</summary>
        private sealed class ResilientStore : IRandomiserStore
        {
            private readonly IRandomiserStore inner;
            private readonly IPlayniteAPI api;
            private bool warned;

            public ResilientStore(IRandomiserStore inner, IPlayniteAPI api)
            {
                this.inner = inner;
                this.api = api;
            }

            public StoreLoadResult Load() => inner.Load();

            public void Save(RandomiserData data)
            {
                try
                {
                    inner.Save(data);
                    warned = false;
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Game Randomiser failed to save its data.");
                    if (!warned)
                    {
                        warned = true;
                        api.Notifications.Add(new NotificationMessage("GameRandomiser_SaveFailed",
                            $"Game Randomiser couldn't save your wheels: {e.Message}", NotificationType.Error));
                    }
                }
            }
        }
    }
}
