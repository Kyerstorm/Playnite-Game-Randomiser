using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Services;
using GameRandomiser.Services;
using Playnite.SDK;

namespace GameRandomiser.UI
{
    public enum SidebarTab
    {
        Wheel,
        Manage,
        History
    }

    public sealed class WheelListItem : BindableBase
    {
        private string name;
        private string icon;
        private int count;
        private bool isActive;
        private string description;

        public WheelListItem(Guid id) => Id = id;

        public Guid Id { get; }
        public string Name { get => name; set => Set(ref name, value); }
        public string Icon { get => icon; set => Set(ref icon, value); }
        public int Count { get => count; set { if (Set(ref count, value)) OnPropertyChanged(nameof(CountText)); } }
        public string CountText => RandomiserContext.Plural(Count, "game");
        public bool IsActive { get => isActive; set => Set(ref isActive, value); }
        public string Description { get => description; set { if (Set(ref description, value)) OnPropertyChanged(nameof(HasDescription)); } }
        public bool HasDescription => !string.IsNullOrEmpty(Description);
        public string AccessibleName => $"{Name}, {CountText}";
    }

    public sealed class ManageGameItem : BindableBase
    {
        private bool isChecked;

        public ManageGameItem(GameInfo game) => Game = game;

        public GameInfo Game { get; }
        public Guid Id => Game.Id;
        public string Name => Game.Name;
        public bool IsInstalled => Game.IsInstalled;
        public string InstallText => Game.IsInstalled ? "Installed" : "Not installed";
        public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }
    }

    public sealed class HistoryItem
    {
        public int Number { get; set; }
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string WhenText { get; set; }
        public bool InLibrary { get; set; }
        public string StatusText { get; set; }
        public BitmapSource Cover { get; set; }
        public bool HasCover => Cover != null;
    }

    public sealed class WinnerViewModel : BindableBase
    {
        private bool wasRemoved;
        private string statusText;
        private bool canLaunch;
        private bool canInstall;

        public GameInfo Game { get; set; }
        public string Name => Game?.Name;
        public BitmapSource Cover { get; set; }
        public bool HasCover => Cover != null;
        public string WheelName { get; set; }
        public bool AskToRemove { get; set; }
        public bool IsInstalled => Game?.IsInstalled == true;
        public string StatusText { get => statusText; set => Set(ref statusText, value); }
        public bool CanLaunch { get => canLaunch; set => Set(ref canLaunch, value); }
        public bool CanInstall { get => canInstall; set => Set(ref canInstall, value); }

        public bool WasRemoved
        {
            get => wasRemoved;
            set
            {
                if (Set(ref wasRemoved, value))
                {
                    OnPropertyChanged(nameof(CanRemove));
                    OnPropertyChanged(nameof(ShowAskPrompt));
                }
            }
        }

        public bool CanRemove => !WasRemoved;
        public bool ShowAskPrompt => AskToRemove && !WasRemoved;
    }

    /// <summary>
    /// State and behaviour behind the Randomiser view. The wheel control itself is driven from the
    /// view's code-behind via events (it is a render surface, not a bindable control).
    /// </summary>
    public sealed class SidebarViewModel : BindableBase, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly RandomiserContext context;
        private readonly ObservableCollection<ManageGameItem> manageGames = new ObservableCollection<ManageGameItem>();
        private IReadOnlyList<GameInfo> entries = new GameInfo[0];
        private IReadOnlyList<GameInfo> spinningEntries;
        private Guid spinningWheelId;
        private WheelListItem selectedWheel;
        private bool suppressSelection;
        private bool isSpinning;
        private bool pendingRefresh;
        private SidebarTab tab = SidebarTab.Wheel;
        private Option<SortMode> selectedSort;
        private string manageSearch = string.Empty;
        private WinnerViewModel winner;
        private bool isWinnerVisible;
        private ThemePalette palette;
        private DispatcherTimer revealTimer;

        public SidebarViewModel(RandomiserContext context)
        {
            this.context = context;
            ManageGamesView = CollectionViewSource.GetDefaultView(manageGames);
            ManageGamesView.Filter = o => o is ManageGameItem item
                && (string.IsNullOrWhiteSpace(ManageSearch) || item.Name.IndexOf(ManageSearch.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);

            context.Wheels.Changed += OnWheelsChanged;
            context.SettingsChanged += OnSettingsChanged;
            context.LibraryChanged += OnLibraryChanged;
            palette = ThemeService.Resolve(context.Settings);
            RefreshAll();
        }

        public event EventHandler EntriesChanged;
        public event EventHandler AppearanceChanged;
        public event EventHandler WinnerRevealed;

        public RandomiserContext Context => context;

        public ObservableCollection<WheelListItem> Wheels { get; } = new ObservableCollection<WheelListItem>();

        public WheelListItem SelectedWheel
        {
            get => selectedWheel;
            set
            {
                if (value == null || suppressSelection || value == selectedWheel)
                {
                    return;
                }

                if (IsSpinning)
                {
                    OnPropertyChanged(); // bounce the selector back
                    return;
                }

                context.SafeRun("switch wheels", () => context.Wheels.SetActiveWheel(value.Id));
            }
        }

        public RandomiserWheel ActiveWheel => context.Wheels.ActiveWheel;

        public IReadOnlyList<GameInfo> Entries => entries;

        public bool HasWheels => Wheels.Count > 0;
        public bool IsFirstRun => Wheels.Count == 0;
        public bool HasEntries => entries.Count > 0;
        public bool IsWheelEmpty => HasWheels && entries.Count == 0;
        public bool ShowWheel => HasWheels;

        public string GameCountText => HasWheels ? RandomiserContext.Plural(entries.Count, "game") : string.Empty;

        public string ActiveWheelName => ActiveWheel?.Name ?? "No wheel";

        public IReadOnlyList<Option<SortMode>> SortOptions { get; } = new[]
        {
            new Option<SortMode>(SortMode.Alphabetical, "A–Z"),
            new Option<SortMode>(SortMode.Random, "Random"),
            new Option<SortMode>(SortMode.Library, "Library order")
        };

        public Option<SortMode> SelectedSort
        {
            get => selectedSort;
            set
            {
                if (value == null || value == selectedSort)
                {
                    return;
                }

                selectedSort = value;
                OnPropertyChanged();
                var wheel = ActiveWheel;
                if (!suppressSelection && wheel != null && !IsSpinning)
                {
                    context.SafeRun("sort the wheel", () => context.Wheels.SetSortMode(wheel.Id, value.Value));
                }
            }
        }

        public bool IsSpinning
        {
            get => isSpinning;
            private set
            {
                if (Set(ref isSpinning, value))
                {
                    OnPropertyChanged(nameof(CanSpin));
                    OnPropertyChanged(nameof(CanEdit));
                }
            }
        }

        public bool CanSpin => HasEntries && !IsSpinning;
        public bool CanEdit => !IsSpinning;

        public SidebarTab Tab
        {
            get => tab;
            set
            {
                if (Set(ref tab, value))
                {
                    OnPropertyChanged(nameof(IsWheelTab));
                    OnPropertyChanged(nameof(IsManageTab));
                    OnPropertyChanged(nameof(IsHistoryTab));
                }
            }
        }

        public bool IsWheelTab { get => Tab == SidebarTab.Wheel; set { if (value) Tab = SidebarTab.Wheel; } }
        public bool IsManageTab { get => Tab == SidebarTab.Manage; set { if (value) Tab = SidebarTab.Manage; } }
        public bool IsHistoryTab { get => Tab == SidebarTab.History; set { if (value) Tab = SidebarTab.History; } }

        // ---- Manage tab ----

        public ICollectionView ManageGamesView { get; }

        public string ManageSearch
        {
            get => manageSearch;
            set
            {
                if (Set(ref manageSearch, value ?? string.Empty))
                {
                    ManageGamesView.Refresh();
                }
            }
        }

        public string ManageHeader => ActiveWheel == null ? string.Empty : $"{ActiveWheel.Name} — {RandomiserContext.Plural(ActiveWheel.GameIds.Count, "game")}".ToUpperInvariant();

        public string ActiveWheelDescription => ActiveWheel?.Population?.Description;

        public bool CanRefreshFromCriteria => ActiveWheel?.Population != null;

        public bool HasManageGames => manageGames.Count > 0;

        // ---- History ----

        public ObservableCollection<HistoryItem> HistoryItems { get; } = new ObservableCollection<HistoryItem>();

        public IEnumerable<HistoryItem> RecentItems => HistoryItems.Take(8);

        public bool HasHistory => HistoryItems.Count > 0;

        public string HistoryHeader => ActiveWheel == null ? "RECENT PICKS" : $"RECENT PICKS — {ActiveWheel.Name.ToUpperInvariant()}";

        // ---- Winner ----

        public WinnerViewModel Winner { get => winner; private set => Set(ref winner, value); }

        public bool IsWinnerVisible { get => isWinnerVisible; private set => Set(ref isWinnerVisible, value); }

        public bool IsCompactWinner => context.Settings.WinnerPresentation == WinnerPresentation.Compact;

        // ---- Appearance ----

        public ThemePalette Palette => palette;

        public WheelAppearance BuildAppearance() => new WheelAppearance
        {
            SegmentColours = palette.SegmentColours,
            Accent = palette.Accent,
            Surface = palette.Surface,
            Border = palette.Border,
            Text = palette.Text,
            DisplayMode = context.Settings.DisplayMode,
            ShowSegmentBorders = context.Settings.ShowSegmentBorders
        };

        // ---- Spinning ----

        /// <summary>Step 1+2: choose the winner and compute the landing rotation. Returns null if a spin isn't possible.</summary>
        public SpinPlan BeginSpin(double currentRotation)
        {
            if (!CanSpin || ActiveWheel == null)
            {
                return null;
            }

            HideWinner();
            spinningEntries = entries;
            spinningWheelId = ActiveWheel.Id;
            IsSpinning = true;
            return context.Randomiser.PlanSpin(spinningEntries.Count, currentRotation, context.Settings.ToSpinOptions());
        }

        /// <summary>Step 4: the wheel has stopped. Records history and reveals the winner shortly after.</summary>
        public void CompleteSpin(SpinPlan plan)
        {
            IsSpinning = false;
            var pool = spinningEntries;
            spinningEntries = null;
            if (plan == null || pool == null || plan.WinnerIndex >= pool.Count)
            {
                ApplyPendingRefresh();
                return;
            }

            var picked = pool[plan.WinnerIndex];
            var game = context.Catalog.TryGet(picked.Id) ?? picked;
            var wheel = context.Wheels.GetWheel(spinningWheelId);

            context.SafeRun("record the spin", () =>
            {
                if (wheel != null)
                {
                    context.History.Record(wheel.Id, game);
                }
            });
            context.Audio.PlayWinner();

            var settings = context.Settings;
            var model = new WinnerViewModel
            {
                Game = game,
                Cover = context.Images.Get(game.CoverPath, 400) ?? context.Images.Get(game.IconPath, 192),
                WheelName = wheel?.Name,
                AskToRemove = settings.WinnerBehaviour == WinnerBehaviour.AskMe
            };
            UpdateWinnerStatus(model);
            Winner = model;

            // Give the eye a moment on the highlighted segment before the card animates in.
            revealTimer?.Stop();
            revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(plan.SegmentCount == 1 ? 150 : 550) };
            revealTimer.Tick += (s, e) =>
            {
                revealTimer.Stop();
                if (Winner != model)
                {
                    return;
                }

                IsWinnerVisible = true;
                WinnerRevealed?.Invoke(this, EventArgs.Empty);

                if (settings.WinnerBehaviour == WinnerBehaviour.RemoveAutomatically && wheel != null)
                {
                    context.SafeRun("remove the winner", () => context.Wheels.RemoveGames(wheel.Id, new[] { game.Id }));
                    model.WasRemoved = true;
                }

                if (settings.AutoLaunchWinner && context.Actions.CanLaunch(game.Id))
                {
                    context.Actions.Launch(game.Id);
                    UpdateWinnerStatus(model);
                }
            };
            revealTimer.Start();
            ApplyPendingRefresh();
        }

        public void HideWinner()
        {
            revealTimer?.Stop();
            IsWinnerVisible = false;
        }

        public void RemoveWinner()
        {
            var model = Winner;
            var wheel = context.Wheels.GetWheel(spinningWheelId);
            if (model?.Game == null || wheel == null)
            {
                return;
            }

            context.SafeRun("remove the game", () => context.Wheels.RemoveGames(wheel.Id, new[] { model.Game.Id }));
            model.WasRemoved = true;
            HideWinner();
        }

        public void UndoRemoveWinner()
        {
            var model = Winner;
            var wheel = context.Wheels.GetWheel(spinningWheelId);
            if (model?.Game == null || wheel == null)
            {
                return;
            }

            context.SafeRun("restore the game", () => context.Wheels.AddGames(wheel.Id, new[] { model.Game.Id }));
            model.WasRemoved = false;
        }

        public void LaunchWinner()
        {
            if (Winner?.Game == null)
            {
                return;
            }

            context.Actions.Launch(Winner.Game.Id);
            HideWinner();
        }

        public void InstallWinner()
        {
            if (Winner?.Game == null)
            {
                return;
            }

            context.Actions.Install(Winner.Game.Id);
            UpdateWinnerStatus(Winner);
        }

        public void ViewWinnerDetails()
        {
            if (Winner?.Game == null)
            {
                return;
            }

            context.Actions.ViewDetails(Winner.Game.Id);
            HideWinner();
        }

        private void UpdateWinnerStatus(WinnerViewModel model)
        {
            var live = context.Catalog.GetPlayniteGame(model.Game.Id);
            if (live == null)
            {
                model.StatusText = "No longer in your library";
                model.CanLaunch = model.CanInstall = false;
                return;
            }

            model.CanLaunch = context.Actions.CanLaunch(live.Id);
            model.CanInstall = context.Actions.CanInstall(live.Id);
            model.StatusText = live.IsRunning ? "Running"
                : live.IsLaunching ? "Launching…"
                : live.IsInstalling ? "Installing…"
                : live.IsInstalled ? "Installed"
                : "Not installed";
        }

        // ---- Wheel management ----

        public void CreateWheel() => context.CreateWheelInteractive();

        public void RenameWheel()
        {
            var wheel = ActiveWheel;
            if (wheel == null)
            {
                return;
            }

            var result = context.Api.Dialogs.SelectString("Wheel name:", "Rename wheel", wheel.Name);
            if (result.Result)
            {
                context.SafeRun("rename the wheel", () => context.Wheels.RenameWheel(wheel.Id, result.SelectedString));
            }
        }

        public void SetWheelIcon(string icon)
        {
            var wheel = ActiveWheel;
            if (wheel != null)
            {
                context.SafeRun("change the icon", () => context.Wheels.SetWheelIcon(wheel.Id, icon));
            }
        }

        public void DuplicateWheel()
        {
            var wheel = ActiveWheel;
            if (wheel != null)
            {
                context.SafeRun("duplicate the wheel", () => context.Wheels.DuplicateWheel(wheel.Id));
            }
        }

        public void DeleteWheel()
        {
            var wheel = ActiveWheel;
            if (wheel == null)
            {
                return;
            }

            var answer = context.Api.Dialogs.ShowMessage(
                $"Delete the \"{wheel.Name}\" wheel and its spin history?\n\nYour games stay in your Playnite library.",
                "Delete wheel", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                context.SafeRun("delete the wheel", () => context.Wheels.DeleteWheel(wheel.Id));
            }
        }

        public void ClearWheel()
        {
            var wheel = ActiveWheel;
            if (wheel == null || wheel.GameIds.Count == 0)
            {
                return;
            }

            var answer = context.Api.Dialogs.ShowMessage(
                $"Remove all {wheel.GameIds.Count} games from \"{wheel.Name}\"?\n\nThis does not affect your Playnite library.",
                "Clear wheel", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                context.SafeRun("clear the wheel", () => context.Wheels.ClearGames(wheel.Id));
            }
        }

        public void AddGames() => context.AddGamesInteractive(ActiveWheel);

        public void RemoveCheckedGames()
        {
            var wheel = ActiveWheel;
            var ids = manageGames.Where(g => g.IsChecked).Select(g => g.Id).ToList();
            if (wheel == null || ids.Count == 0)
            {
                return;
            }

            context.SafeRun("remove games", () => context.Wheels.RemoveGames(wheel.Id, ids));
        }

        public void SetAllChecked(bool isChecked)
        {
            foreach (var item in ManageGamesView.Cast<ManageGameItem>())
            {
                item.IsChecked = isChecked;
            }
        }

        public int CheckedCount => manageGames.Count(g => g.IsChecked);

        /// <summary>Adds any library games that now match the criteria this wheel was created from.</summary>
        public void RefreshFromCriteria()
        {
            var wheel = ActiveWheel;
            if (wheel?.Population == null)
            {
                return;
            }

            context.SafeRun("refresh the wheel", () =>
            {
                var matches = context.Population.Evaluate(wheel.Population).Select(g => g.Id).ToList();
                var result = context.Wheels.AddGames(wheel.Id, matches);
                context.Api.Dialogs.ShowMessage(
                    result.Added == 0
                        ? "The wheel is already up to date with its criteria."
                        : $"Added {RandomiserContext.Plural(result.Added, "new matching game")} to {wheel.Name}.",
                    "Game Randomiser");
            });
        }

        public void Shuffle()
        {
            var wheel = ActiveWheel;
            if (wheel != null && !IsSpinning && wheel.GameIds.Count > 1)
            {
                context.SafeRun("shuffle the wheel", () => context.Wheels.Shuffle(wheel.Id));
            }
        }

        public void ClearHistory()
        {
            var wheel = ActiveWheel;
            if (wheel == null || wheel.History.Count == 0)
            {
                return;
            }

            var answer = context.Api.Dialogs.ShowMessage($"Clear the spin history for \"{wheel.Name}\"?", "Clear history",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                context.SafeRun("clear history", () => context.History.Clear(wheel.Id));
            }
        }

        public void ShowHistoryItemDetails(HistoryItem item)
        {
            if (item != null && item.InLibrary)
            {
                context.Actions.ViewDetails(item.GameId);
            }
        }

        // ---- Refresh plumbing ----

        private void OnWheelsChanged(object sender, WheelsChangedEventArgs e)
        {
            if (e.Kind == WheelChangeKind.HistoryChanged)
            {
                RefreshHistory();
                return;
            }

            RefreshWheelList();
            if (IsSpinning)
            {
                pendingRefresh = true;
                return;
            }

            RefreshEntries();
            RefreshManage();
            RefreshHistory();
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            palette = ThemeService.Resolve(context.Settings);
            OnPropertyChanged(nameof(Palette));
            OnPropertyChanged(nameof(IsCompactWinner));
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnLibraryChanged(object sender, EventArgs e)
        {
            if (IsSpinning)
            {
                pendingRefresh = true;
                return;
            }

            RefreshAll();
        }

        private void ApplyPendingRefresh()
        {
            if (!pendingRefresh)
            {
                return;
            }

            pendingRefresh = false;
            RefreshAll();
        }

        public void RefreshAll()
        {
            RefreshWheelList();
            RefreshEntries();
            RefreshManage();
            RefreshHistory();
        }

        private void RefreshWheelList()
        {
            suppressSelection = true;
            try
            {
                var wheels = context.Wheels.Wheels;
                var active = context.Wheels.ActiveWheel;
                for (var i = Wheels.Count - 1; i >= 0; i--)
                {
                    if (wheels.All(w => w.Id != Wheels[i].Id))
                    {
                        Wheels.RemoveAt(i);
                    }
                }

                for (var i = 0; i < wheels.Count; i++)
                {
                    var wheel = wheels[i];
                    var item = Wheels.FirstOrDefault(w => w.Id == wheel.Id);
                    if (item == null)
                    {
                        item = new WheelListItem(wheel.Id);
                        Wheels.Insert(i, item);
                    }
                    else if (Wheels.IndexOf(item) != i)
                    {
                        Wheels.Move(Wheels.IndexOf(item), i);
                    }

                    item.Name = wheel.Name;
                    item.Icon = wheel.Icon;
                    item.Count = wheel.GameIds.Count;
                    item.Description = wheel.Population?.Description;
                    item.IsActive = active != null && wheel.Id == active.Id;
                }

                selectedWheel = active == null ? null : Wheels.FirstOrDefault(w => w.Id == active.Id);
                OnPropertyChanged(nameof(SelectedWheel));

                var sortMode = active?.SortMode ?? SortMode.Alphabetical;
                selectedSort = SortOptions.First(o => o.Value == sortMode);
                OnPropertyChanged(nameof(SelectedSort));
            }
            finally
            {
                suppressSelection = false;
            }

            OnPropertyChanged(nameof(HasWheels));
            OnPropertyChanged(nameof(IsFirstRun));
            OnPropertyChanged(nameof(ShowWheel));
            OnPropertyChanged(nameof(ActiveWheelName));
            OnPropertyChanged(nameof(ActiveWheelDescription));
            OnPropertyChanged(nameof(CanRefreshFromCriteria));
            OnPropertyChanged(nameof(ManageHeader));
            OnPropertyChanged(nameof(HistoryHeader));
        }

        private void RefreshEntries()
        {
            var wheel = ActiveWheel;
            entries = wheel == null ? new GameInfo[0] : context.Wheels.ResolveEntries(wheel.Id);
            OnPropertyChanged(nameof(Entries));
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(IsWheelEmpty));
            OnPropertyChanged(nameof(CanSpin));
            OnPropertyChanged(nameof(GameCountText));
            EntriesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshManage()
        {
            var checkedIds = new HashSet<Guid>(manageGames.Where(g => g.IsChecked).Select(g => g.Id));
            manageGames.Clear();
            foreach (var game in entries.OrderBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase))
            {
                manageGames.Add(new ManageGameItem(game) { IsChecked = checkedIds.Contains(game.Id) });
            }

            OnPropertyChanged(nameof(HasManageGames));
            OnPropertyChanged(nameof(ManageHeader));
        }

        private void RefreshHistory()
        {
            HistoryItems.Clear();
            var wheel = ActiveWheel;
            if (wheel != null)
            {
                var number = 1;
                foreach (var entry in context.History.GetHistory(wheel.Id))
                {
                    var live = context.Catalog.TryGet(entry.GameId);
                    HistoryItems.Add(new HistoryItem
                    {
                        Number = number++,
                        GameId = entry.GameId,
                        Name = live?.Name ?? entry.GameName ?? "Unknown game",
                        WhenText = entry.SpunAtUtc.ToLocalTime().ToString("d MMM yyyy · HH:mm", CultureInfo.CurrentCulture),
                        InLibrary = live != null,
                        StatusText = live == null ? "No longer in library" : live.IsInstalled ? "Installed" : "Not installed",
                        Cover = number <= 60 ? context.Images.TryGet(live?.CoverPath, 96) : null
                    });
                }

                // Load any missing thumbnails in the background and refresh once.
                var missing = context.History.GetHistory(wheel.Id).Take(60)
                    .Select(h => context.Catalog.TryGet(h.GameId)?.CoverPath)
                    .Where(p => p != null && context.Images.TryGet(p, 96) == null)
                    .ToList();
                if (missing.Count > 0)
                {
                    context.Images.Prefetch(missing, 96, context.Dispatcher, RefreshHistory);
                }
            }

            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(RecentItems));
            OnPropertyChanged(nameof(HistoryHeader));
        }

        public void Dispose()
        {
            revealTimer?.Stop();
            context.Wheels.Changed -= OnWheelsChanged;
            context.SettingsChanged -= OnSettingsChanged;
            context.LibraryChanged -= OnLibraryChanged;
        }
    }
}
