using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
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
        /// <summary>Quiet period after the last library event before dynamic wheels are refreshed.</summary>
        internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(750);

        private const string SaveFailedNotification = "GameRandomiser_SaveFailed";
        private const string RefreshNotificationPrefix = "GameRandomiser_Refresh_";

        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly RandomiserSettingsViewModel settingsViewModel;
        private readonly CryptoRandomSource random = new CryptoRandomSource();
        private readonly ErrorReporter errors = new ErrorReporter();
        private DispatcherTimer libraryDebounce;
        private bool initialized;
        private bool disposed;
        private bool saveWarned;

        public RandomiserContext(GameRandomiserPlugin plugin, IPlayniteAPI api, RandomiserSettingsViewModel settingsViewModel)
        {
            Plugin = plugin;
            Api = api;
            this.settingsViewModel = settingsViewModel;

            Catalog = new PlayniteGameCatalog(api);
            var store = new JsonFileStore(Path.Combine(plugin.GetPluginUserDataPath(), "data.json"));
            Wheels = new WheelService(store, Catalog, random);
            Wheels.SaveFailed += OnSaveFailed;
            Wheels.Changed += OnWheelsChanged;
            History = new HistoryService(Wheels);
            Randomiser = new RandomiserService(random);
            Reroll = new RerollProtectionService(Wheels);
            Population = new PopulationEngine(Catalog);
            Refresh = new RefreshCoordinator(Wheels, Population, Catalog,
                new DispatcherRefreshScheduler(Dispatcher, RefreshDebounce), random, errors);
            Refresh.Completed += OnRefreshCompleted;
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
        public RerollProtectionService Reroll { get; }
        public PopulationEngine Population { get; }

        /// <summary>The only route by which dynamic wheels are refreshed.</summary>
        public RefreshCoordinator Refresh { get; }

        public PlayniteGameActions Actions { get; }
        public AudioService Audio { get; }
        public ImageCache Images { get; }
        public IErrorReporter Errors => errors;

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

            if (Settings.Reroll.ResetBehaviour == RerollResetBehaviour.ResetOnRestart)
            {
                SafeRun("reset reroll protection", () => Reroll.ResetAll());
            }

            var defaultWheel = Settings.DefaultWheelId;
            if (defaultWheel.HasValue && Wheels.GetWheel(defaultWheel.Value) != null)
            {
                SafeRun("select default wheel", () => Wheels.SetActiveWheel(defaultWheel.Value));
            }

            if (!string.IsNullOrEmpty(Wheels.LoadWarning))
            {
                Api.Notifications.Add(new NotificationMessage("GameRandomiser_LoadWarning", Wheels.LoadWarning, NotificationType.Error));
            }

            // The library may have changed while Playnite was closed. Debounced, so startup isn't held up.
            Refresh.InvalidateAll(RefreshReason.Startup);
        }

        /// <summary>Records a failure to start up, without stopping Playnite from loading.</summary>
        internal void ReportStartupFailure(Exception exception)
        {
            const string Message = "Game Randomiser could not finish starting up. Your saved wheels have not been changed. Restart Playnite, and check extensions.log if this keeps happening.";
            errors.Report(new RandomiserError(ErrorCategory.Initialisation, "initialise extension", Message, exception, recovery: "continuing with reduced functionality"));
            try
            {
                Api.Notifications.Add(new NotificationMessage("GameRandomiser_Startup", Message, NotificationType.Error));
            }
            catch (Exception e)
            {
                Logger.Error(e, "Game Randomiser could not show its startup notification.");
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
            var added = e.AddedItems?.Count ?? 0;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed)
                {
                    return;
                }

                if (removed.Count > 0)
                {
                    SafeRun("remove deleted games", () => Wheels.ForgetGames(removed));
                }

                if (removed.Count > 0 || added > 0)
                {
                    Refresh.InvalidateLibrary(LibraryFields.Collection);
                }

                ScheduleLibraryRefresh();
            }));
        }

        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            var nowHidden = new List<Guid>();
            var relevant = false;
            var changed = LibraryFields.None;
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

                changed |= ChangedFields(oldData, newData);
            }

            if (nowHidden.Count == 0 && !relevant && changed == LibraryFields.None)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed)
                {
                    return;
                }

                if (nowHidden.Count > 0)
                {
                    // Keep wheel data clean: hidden games are never eligible.
                    SafeRun("remove hidden games", () => Wheels.RemoveGamesFromAllWheels(nowHidden));
                }

                // Only wheels whose criteria read one of the changed aspects are refreshed.
                Refresh.InvalidateLibrary(changed);
                if (nowHidden.Count > 0 || relevant)
                {
                    ScheduleLibraryRefresh();
                }
            }));
        }

        /// <summary>Which aspects of a game that population rules read are different after an update.</summary>
        private static LibraryFields ChangedFields(Game before, Game after)
        {
            if (before == null)
            {
                return LibraryFields.All;
            }

            var changed = LibraryFields.None;
            if (before.Hidden != after.Hidden) changed |= LibraryFields.Hidden;
            if (before.IsInstalled != after.IsInstalled) changed |= LibraryFields.Installed;
            if (before.Playtime != after.Playtime) changed |= LibraryFields.Playtime;
            if (before.PlayCount != after.PlayCount || before.LastActivity != after.LastActivity) changed |= LibraryFields.Activity;
            if (before.CompletionStatusId != after.CompletionStatusId) changed |= LibraryFields.CompletionStatus;
            if (!SameIds(before.GenreIds, after.GenreIds)) changed |= LibraryFields.Genres;
            if (!SameIds(before.PlatformIds, after.PlatformIds)) changed |= LibraryFields.Platforms;
            if (!SameIds(before.TagIds, after.TagIds)) changed |= LibraryFields.Tags;
            if (!SameIds(before.CategoryIds, after.CategoryIds)) changed |= LibraryFields.Categories;
            return changed;
        }

        private static bool SameIds(List<Guid> a, List<Guid> b)
        {
            var countA = a?.Count ?? 0;
            var countB = b?.Count ?? 0;
            if (countA != countB)
            {
                return false;
            }

            return countA == 0 || new HashSet<Guid>(a).SetEquals(b);
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

        // ---- Refresh and save feedback ----

        /// <summary>Re-evaluates a wheel's criteria now and tells the user what happened.</summary>
        public void RefreshWheelNow(RandomiserWheel wheel)
        {
            if (wheel?.Population != null)
            {
                Refresh.InvalidateWheel(wheel.Id, RefreshReason.Manual);
            }
        }

        private void OnRefreshCompleted(object sender, RefreshBatchEventArgs e)
        {
            foreach (var result in e.Results)
            {
                if (result.Kind == RefreshResultKind.Superseded || result.Kind == RefreshResultKind.Skipped)
                {
                    continue;
                }

                var wheel = Wheels.GetWheel(result.WheelId);
                var notificationId = RefreshNotificationPrefix + result.WheelId;
                var failed = result.Kind == RefreshResultKind.Failed;
                if (result.Reason == RefreshReason.Manual)
                {
                    // The user asked for this one, so answer directly, once the batch has been applied.
                    var captured = result;
                    Dispatcher.BeginInvoke(new Action(() => ShowRefreshSummary(wheel, captured)));
                }
                else if (failed)
                {
                    // Same id each time: a wheel that keeps failing updates one notification instead of piling them up.
                    Api.Notifications.Add(new NotificationMessage(notificationId,
                        $"Game Randomiser, {wheel?.Name ?? "wheel"}: {result.ErrorMessage}", NotificationType.Error));
                }

                if (!failed)
                {
                    Api.Notifications.Remove(notificationId);
                }
            }
        }

        private void ShowRefreshSummary(RandomiserWheel wheel, WheelRefreshResult result)
        {
            if (disposed)
            {
                return;
            }

            switch (result.Kind)
            {
                case RefreshResultKind.Failed:
                    Api.Dialogs.ShowErrorMessage(result.ErrorMessage, "Game Randomiser");
                    break;
                case RefreshResultKind.Unchanged:
                    Api.Dialogs.ShowMessage("The wheel is already up to date with its criteria.", "Game Randomiser");
                    break;
                case RefreshResultKind.Updated:
                    var parts = new List<string>();
                    if (result.Added > 0)
                    {
                        parts.Add($"added {Plural(result.Added, "game")}");
                    }

                    if (result.Removed > 0)
                    {
                        parts.Add($"removed {Plural(result.Removed, "game")}");
                    }

                    Api.Dialogs.ShowMessage(
                        parts.Count == 0
                            ? "The wheel was refreshed."
                            : $"{wheel?.Name ?? "The wheel"} was refreshed: {string.Join(" and ", parts)}.",
                        "Game Randomiser");
                    break;
            }
        }

        private void OnSaveFailed(object sender, SaveFailedEventArgs e)
        {
            errors.Report(new RandomiserError(ErrorCategory.Persistence, "save wheels", UserMessages.SaveFailed, e.Exception,
                recovery: "kept changes in memory; previous file intact"));
            if (saveWarned)
            {
                return;
            }

            saveWarned = true;
            Api.Notifications.Add(new NotificationMessage(SaveFailedNotification, "Game Randomiser: " + UserMessages.SaveFailed, NotificationType.Error));
        }

        private void OnWheelsChanged(object sender, WheelsChangedEventArgs e)
        {
            if (saveWarned && !Wheels.HasUnsavedChanges)
            {
                // A later save went through and wrote everything, so the warning no longer applies.
                saveWarned = false;
                Api.Notifications.Remove(SaveFailedNotification);
            }
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
                if (result.Added == 0 && result.AlreadyPresent > 0 && result.Ineligible == 0 && result.NotMatching == 0)
                {
                    var message = gameIds.Count == 1
                        ? $"\"{Catalog.TryGet(gameIds[0])?.Name}\" is already on the {wheel.Name} wheel."
                        : $"All {gameIds.Count} games are already on the {wheel.Name} wheel.";
                    Api.Dialogs.ShowMessage(message, "Game Randomiser");
                }
                else if (result.AlreadyPresent > 0 || result.Ineligible > 0 || result.NotMatching > 0)
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

                    if (result.NotMatching > 0)
                    {
                        parts.Add($"{Plural(result.NotMatching, "game doesn't", "games don't")} match this wheel's criteria and "
                            + (result.NotMatching == 1 ? "was" : "were")
                            + " skipped. To add any game, switch the wheel to \"Keep in sync + pinned games\" on the Wheels tab.");
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
                var message = $"Game Randomiser couldn't {what}.\n\n{e.Message}\n\nYour saved wheels have not been changed. Technical details are in Playnite's extensions.log.";
                errors.Report(new RandomiserError(ErrorCategory.General, what, message, e));
                Api.Dialogs.ShowErrorMessage(message, "Game Randomiser");
            }
        }

        public static string Plural(int count, string singular, string plural = null) =>
            count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

        public void Dispose()
        {
            disposed = true;
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
            Refresh.Dispose();
            Wheels.SaveFailed -= OnSaveFailed;
            Wheels.Changed -= OnWheelsChanged;
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

        /// <summary>Writes one structured line per failure to Playnite's log. User feedback is the caller's job.</summary>
        private sealed class ErrorReporter : IErrorReporter
        {
            public void Report(RandomiserError error)
            {
                var line = "Game Randomiser " + error.ToLogString();
                if (error.Exception != null)
                {
                    Logger.Error(error.Exception, line);
                }
                else
                {
                    Logger.Error(line);
                }
            }
        }
    }
}
