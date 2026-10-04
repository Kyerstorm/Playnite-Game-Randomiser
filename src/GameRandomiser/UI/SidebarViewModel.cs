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
using GameRandomiser.Core.Diagnostics;
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

    /// <summary>The membership policies offered wherever a criteria wheel is created or edited.</summary>
    public static class MembershipPolicyOptions
    {
        public static IReadOnlyList<Option<MembershipPolicy>> All { get; } = new[]
        {
            new Option<MembershipPolicy>(MembershipPolicy.ManualSnapshot, "Snapshot (update manually)",
                "The list only changes when you edit it or press Refresh from criteria."),
            new Option<MembershipPolicy>(MembershipPolicy.StrictCriteria, "Keep in sync (strict)",
                "Always the games that match the criteria. Games that stop matching leave the wheel automatically."),
            new Option<MembershipPolicy>(MembershipPolicy.CriteriaPlusPinned, "Keep in sync + pinned games",
                "Games that match the criteria, plus any games you add or pin yourself.")
        };
    }

    public sealed class WheelListItem : BindableBase
    {
        private string name;
        private string icon;
        private int count;
        private bool isActive;
        private bool isDynamic;
        private string description;

        public WheelListItem(Guid id) => Id = id;

        public Guid Id { get; }
        public string Name { get => name; set { if (Set(ref name, value)) OnPropertyChanged(nameof(AccessibleName)); } }
        public string Icon { get => icon; set => Set(ref icon, value); }
        public int Count { get => count; set { if (Set(ref count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(AccessibleName)); } } }
        public string CountText => RandomiserContext.Plural(Count, "game");
        public bool IsActive { get => isActive; set => Set(ref isActive, value); }

        /// <summary>True when the wheel keeps itself in sync with its criteria.</summary>
        public bool IsDynamic { get => isDynamic; set { if (Set(ref isDynamic, value)) OnPropertyChanged(nameof(AccessibleName)); } }

        public string Description { get => description; set { if (Set(ref description, value)) OnPropertyChanged(nameof(HasDescription)); } }
        public bool HasDescription => !string.IsNullOrEmpty(Description);
        public string AccessibleName => $"{Name}, {CountText}{(IsDynamic ? ", updates automatically" : string.Empty)}";
    }

    public sealed class ManageGameItem : BindableBase
    {
        private bool isChecked;

        public ManageGameItem(GameInfo game, bool isPinned = false)
        {
            Game = game;
            IsPinned = isPinned;
        }

        public GameInfo Game { get; }
        public Guid Id => Game.Id;
        public string Name => Game.Name;
        public bool IsInstalled => Game.IsInstalled;
        public string InstallText => Game.IsInstalled ? "Installed" : "Not installed";

        /// <summary>Stays on the wheel even when it stops matching the criteria.</summary>
        public bool IsPinned { get; }

        public string AccessibleName => IsPinned ? Name + ", pinned" : Name;
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
        private string protectionText;
        private bool canSpinAgain = true;
        private bool canAccept;
        private bool canResetProtection;

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

        /// <summary>How many games could have won this spin, when reroll protection is on.</summary>
        public int? EligibleCount { get; set; }

        /// <summary>Reroll protection summary for this pick, or null when protection is off.</summary>
        public string ProtectionText { get => protectionText; set => Set(ref protectionText, value); }

        public bool CanSpinAgain { get => canSpinAgain; set => Set(ref canSpinAgain, value); }
        public bool CanAccept { get => canAccept; set => Set(ref canAccept, value); }
        public bool CanResetProtection { get => canResetProtection; set => Set(ref canResetProtection, value); }

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
        private static readonly RefreshInfo NoRefresh = new RefreshInfo(RefreshStatus.NotApplicable, null, null);
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
        private Option<MembershipPolicy> selectedPolicy;
        private string manageSearch = string.Empty;
        private WinnerViewModel winner;
        private bool isWinnerVisible;
        private ThemePalette palette;
        private DispatcherTimer revealTimer;
        private DispatcherTimer cooldownTimer;
        private IDisposable spinSuspension;
        private SelectionResult selection = new SelectionResult();
        private RefreshInfo refreshInfo = NoRefresh;
        private string protectionStatusText;

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

        /// <summary>A spin is possible: there are games, nothing is spinning and reroll protection allows it.</summary>
        public bool CanSpin => HasEntries && !IsSpinning && !selection.IsBlocked;

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

        // ---- Dynamic membership ----

        public IReadOnlyList<Option<MembershipPolicy>> PolicyOptions => MembershipPolicyOptions.All;

        public Option<MembershipPolicy> SelectedPolicy
        {
            get => selectedPolicy;
            set
            {
                if (value == null || suppressSelection || value == selectedPolicy)
                {
                    return;
                }

                ChangePolicy(value.Value);
            }
        }

        public bool IsActiveWheelDynamic => ActiveWheel?.IsDynamic == true;

        /// <summary>The games shown may no longer match the library because the last refresh failed.</summary>
        public bool IsRefreshStale => refreshInfo.IsStale;

        public bool ShowRefreshStatus => refreshInfo.Status != RefreshStatus.NotApplicable;

        /// <summary>One line on the Wheels tab: where the list stands and when it was last confirmed.</summary>
        public string RefreshStatusText
        {
            get
            {
                var when = refreshInfo.LastRefreshUtc?.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
                switch (refreshInfo.Status)
                {
                    case RefreshStatus.Refreshing: return "⟳ Refreshing…";
                    case RefreshStatus.Refreshed: return $"✓ Up to date. Last checked {when}.";
                    case RefreshStatus.Ready: return "Waiting for the first refresh.";
                    case RefreshStatus.Failed: return "⚠ Refresh failed. " + refreshInfo.Error;
                    case RefreshStatus.FailedUsingLastKnownGood:
                        return $"⚠ Refresh failed, so this list may be out of date. Showing the games from {when}.";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>Secondary detail for tooltips: the failure reason, or what the policy does.</summary>
        public string RefreshStatusDetail =>
            refreshInfo.Error ?? selectedPolicy?.Description ?? string.Empty;

        /// <summary>Compact marker beside the game count on the wheel tab. Null for ordinary wheels.</summary>
        public string DynamicBadgeText
        {
            get
            {
                switch (refreshInfo.Status)
                {
                    case RefreshStatus.NotApplicable: return null;
                    case RefreshStatus.Refreshing: return "auto · refreshing…";
                    case RefreshStatus.Failed:
                    case RefreshStatus.FailedUsingLastKnownGood: return "⚠ auto · may be out of date";
                    default: return "auto";
                }
            }
        }

        public string DynamicBadgeToolTip =>
            refreshInfo.Status == RefreshStatus.NotApplicable
                ? null
                : $"This wheel keeps itself in sync with its criteria ({ActiveWheelDescription}).\n{RefreshStatusText}"
                  + (refreshInfo.Error == null ? string.Empty : "\n" + refreshInfo.Error);

        public bool CanPin => ActiveWheel?.MembershipPolicy == MembershipPolicy.CriteriaPlusPinned && ActiveWheel.Population != null;

        public int RemovedCount => IsActiveWheelDynamic ? ActiveWheel.ExcludedGameIds.Count : 0;

        public bool CanRestoreRemoved => RemovedCount > 0;

        public string RestoreRemovedText => "Restore " + RandomiserContext.Plural(RemovedCount, "removed game");

        // ---- Reroll protection ----

        /// <summary>Why the next spin is restricted or blocked, or null when there is nothing to say.</summary>
        public string ProtectionStatusText { get => protectionStatusText; private set { if (Set(ref protectionStatusText, value)) OnPropertyChanged(nameof(HasProtectionStatus)); } }

        public bool HasProtectionStatus => !string.IsNullOrEmpty(ProtectionStatusText);

        public bool IsSpinBlocked => selection.IsBlocked && selection.BlockReason != SpinBlockReason.EmptyWheel;

        public bool CanAcceptPick => context.Settings.Reroll.Enabled && context.Settings.Reroll.LimitRerolls && selection.SessionActive && !IsSpinning;

        public bool CanResetProtection => context.Settings.Reroll.Enabled && ActiveWheel?.Reroll != null && !ActiveWheel.Reroll.IsEmpty && !IsSpinning;

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

        /// <summary>
        /// Steps 1+2: decide who is eligible, choose the winner among them and compute the landing rotation.
        /// Returns null if a spin isn't possible (the reason is shown in <see cref="ProtectionStatusText"/>).
        /// </summary>
        public SpinPlan BeginSpin(double currentRotation)
        {
            if (IsSpinning || ActiveWheel == null || !HasEntries)
            {
                return null;
            }

            // Evaluate at the moment of the spin: cooldowns move with the clock.
            RefreshProtection();
            if (selection.IsBlocked)
            {
                return null;
            }

            HideWinner();
            var eligible = selection.EligibleIndices;
            spinningEntries = entries;
            spinningWheelId = ActiveWheel.Id;
            spinningEligibleCount = selection.ProtectionEnabled ? eligible.Count : (int?)null;
            IsSpinning = true;

            // The wheel being spun must not change underneath the animation; refreshes wait for the result.
            spinSuspension = context.Refresh.Suspend();
            return context.Randomiser.PlanSpin(spinningEntries.Count, eligible, currentRotation, context.Settings.ToSpinOptions());
        }

        private int? spinningEligibleCount;

        /// <summary>Step 4: the wheel has stopped. Records history and reveals the winner shortly after.</summary>
        public void CompleteSpin(SpinPlan plan)
        {
            IsSpinning = false;
            var pool = spinningEntries;
            spinningEntries = null;
            if (plan == null || pool == null || plan.WinnerIndex >= pool.Count)
            {
                EndSpin();
                return;
            }

            var picked = pool[plan.WinnerIndex];
            var game = context.Catalog.TryGet(picked.Id) ?? picked;
            var wheel = context.Wheels.GetWheel(spinningWheelId);

            context.SafeRun("record the spin", () =>
            {
                if (wheel != null)
                {
                    // History and protection state belong to the same spin: one save for both.
                    using (context.Wheels.Batch())
                    {
                        context.History.Record(wheel.Id, game);
                        context.Reroll.RecordSpin(wheel.Id, game.Id, context.Settings.Reroll);
                    }
                }
            });
            context.Audio.PlayWinner();

            var settings = context.Settings;
            var model = new WinnerViewModel
            {
                Game = game,
                Cover = context.Images.Get(game.CoverPath, 400) ?? context.Images.Get(game.IconPath, 192),
                WheelName = wheel?.Name,
                AskToRemove = settings.WinnerBehaviour == WinnerBehaviour.AskMe,
                EligibleCount = spinningEligibleCount
            };
            UpdateWinnerStatus(model);
            Winner = model;
            RefreshProtection();

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
            EndSpin();
        }

        /// <summary>The safe boundary after a spin: held-back membership updates and UI refreshes are applied.</summary>
        private void EndSpin()
        {
            var suspension = spinSuspension;
            spinSuspension = null;
            suspension?.Dispose();
            ApplyPendingRefresh();
            OnPropertyChanged(nameof(CanAcceptPick));
            OnPropertyChanged(nameof(CanResetProtection));
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

            // Launching the pick is accepting it.
            AcceptSession();
            context.Actions.Launch(Winner.Game.Id);
            HideWinner();
        }

        public void InstallWinner()
        {
            if (Winner?.Game == null)
            {
                return;
            }

            AcceptSession();
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

        /// <summary>Keeps the current pick: ends the reroll session so the next spin starts fresh.</summary>
        public void AcceptPick()
        {
            if (IsSpinning)
            {
                return;
            }

            AcceptSession();
            HideWinner();
        }

        /// <summary>Forgets recent winners, the reroll count and any cooldown for the current wheel.</summary>
        public void ResetProtection()
        {
            var wheel = ActiveWheel;
            if (wheel == null || IsSpinning)
            {
                return;
            }

            context.SafeRun("reset reroll protection", () => context.Reroll.Reset(wheel.Id));
        }

        private void AcceptSession()
        {
            var wheel = context.Wheels.GetWheel(spinningWheelId) ?? ActiveWheel;
            if (wheel != null && context.Settings.Reroll.Enabled)
            {
                context.SafeRun("accept the pick", () => context.Reroll.Accept(wheel.Id));
            }
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
            var ids = CheckedIds();
            if (wheel == null || ids.Count == 0)
            {
                return;
            }

            context.SafeRun("remove games", () => context.Wheels.RemoveGames(wheel.Id, ids));
        }

        public void PinCheckedGames()
        {
            var wheel = ActiveWheel;
            var ids = CheckedIds();
            if (wheel != null && ids.Count > 0)
            {
                context.SafeRun("pin games", () => context.Wheels.PinGames(wheel.Id, ids));
            }
        }

        public void UnpinCheckedGames()
        {
            var wheel = ActiveWheel;
            var ids = CheckedIds();
            if (wheel != null && ids.Count > 0)
            {
                context.SafeRun("unpin games", () => context.Wheels.UnpinGames(wheel.Id, ids));
            }
        }

        /// <summary>Lets games the user removed from a dynamic wheel come back if they still match.</summary>
        public void RestoreRemovedGames()
        {
            var wheel = ActiveWheel;
            if (wheel != null)
            {
                context.SafeRun("restore removed games", () => context.Wheels.RestoreRemovedGames(wheel.Id));
            }
        }

        public void SetAllChecked(bool isChecked)
        {
            foreach (var item in ManageGamesView.Cast<ManageGameItem>())
            {
                item.IsChecked = isChecked;
            }
        }

        public int CheckedCount => manageGames.Count(g => g.IsChecked);

        private List<Guid> CheckedIds() => manageGames.Where(g => g.IsChecked).Select(g => g.Id).ToList();

        /// <summary>
        /// Re-evaluates the wheel's criteria now. Snapshot wheels gain newly matching games; dynamic
        /// wheels are fully reconciled. The outcome is reported once the refresh completes.
        /// </summary>
        public void RefreshFromCriteria()
        {
            var wheel = ActiveWheel;
            if (wheel?.Population != null)
            {
                context.SafeRun("refresh the wheel", () => context.RefreshWheelNow(wheel));
            }
        }

        private void ChangePolicy(MembershipPolicy policy)
        {
            var wheel = ActiveWheel;
            if (wheel == null || IsSpinning)
            {
                BounceBack(nameof(SelectedPolicy));
                return;
            }

            var previous = wheel.MembershipPolicy;
            if (policy == MembershipPolicy.StrictCriteria)
            {
                // The one destructive switch: hand-picked and pinned games that don't match will go.
                var answer = context.Api.Dialogs.ShowMessage(
                    $"Keep \"{wheel.Name}\" strictly in sync with its criteria?\n\nGames that don't match \"{wheel.Population?.Description}\" will be removed from this wheel. Your Playnite library is not affected.",
                    "Membership policy", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    BounceBack(nameof(SelectedPolicy));
                    return;
                }
            }

            context.SafeRun("change the membership policy", () =>
            {
                context.Wheels.SetMembershipPolicy(wheel.Id, policy);
                if (policy == MembershipPolicy.CriteriaPlusPinned && previous == MembershipPolicy.ManualSnapshot)
                {
                    // Games added by hand to the snapshot become pins instead of being dropped.
                    context.Refresh.InvalidateWheel(wheel.Id, RefreshReason.RulesChanged, pinUnmatched: true);
                }
            });
            BounceBack(nameof(SelectedPolicy));
        }

        /// <summary>Re-reads a bound selector after the current binding update has finished.</summary>
        private void BounceBack(string propertyName) =>
            context.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(propertyName)));

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
            switch (e.Kind)
            {
                case WheelChangeKind.HistoryChanged:
                    RefreshHistory();
                    return;
                case WheelChangeKind.RefreshStateChanged:
                    RefreshDynamicState();
                    return;
                case WheelChangeKind.ProtectionChanged:
                    RefreshProtection();
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
            RefreshProtection();
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
                    item.IsDynamic = wheel.IsDynamic;
                    item.IsActive = active != null && wheel.Id == active.Id;
                }

                selectedWheel = active == null ? null : Wheels.FirstOrDefault(w => w.Id == active.Id);
                OnPropertyChanged(nameof(SelectedWheel));

                var sortMode = active?.SortMode ?? SortMode.Alphabetical;
                selectedSort = SortOptions.First(o => o.Value == sortMode);
                OnPropertyChanged(nameof(SelectedSort));

                var policy = active?.MembershipPolicy ?? MembershipPolicy.ManualSnapshot;
                selectedPolicy = PolicyOptions.First(o => o.Value == policy);
                OnPropertyChanged(nameof(SelectedPolicy));
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
            OnPropertyChanged(nameof(CanPin));
            OnPropertyChanged(nameof(RemovedCount));
            OnPropertyChanged(nameof(CanRestoreRemoved));
            OnPropertyChanged(nameof(RestoreRemovedText));
            RefreshDynamicState();
        }

        private void RefreshDynamicState()
        {
            var wheel = ActiveWheel;
            refreshInfo = wheel == null ? NoRefresh : context.Wheels.GetRefreshInfo(wheel.Id);
            OnPropertyChanged(nameof(IsActiveWheelDynamic));
            OnPropertyChanged(nameof(IsRefreshStale));
            OnPropertyChanged(nameof(ShowRefreshStatus));
            OnPropertyChanged(nameof(RefreshStatusText));
            OnPropertyChanged(nameof(RefreshStatusDetail));
            OnPropertyChanged(nameof(DynamicBadgeText));
            OnPropertyChanged(nameof(DynamicBadgeToolTip));
        }

        private void RefreshEntries()
        {
            var wheel = ActiveWheel;
            entries = wheel == null ? new GameInfo[0] : context.Wheels.ResolveEntries(wheel.Id);
            OnPropertyChanged(nameof(Entries));
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(IsWheelEmpty));
            OnPropertyChanged(nameof(GameCountText));
            RefreshProtection();
            EntriesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Re-runs the eligibility stage for the games currently shown, so the Spin button, the status
        /// line and the winner card always describe what the next spin would actually do.
        /// </summary>
        private void RefreshProtection()
        {
            var wheel = ActiveWheel;
            var options = context.Settings.Reroll;
            selection = wheel == null
                ? RerollProtectionService.Evaluate(entries, null, options, DateTime.UtcNow)
                : context.Reroll.Evaluate(wheel.Id, entries, options);

            ProtectionStatusText = DescribeProtection(options);
            OnPropertyChanged(nameof(CanSpin));
            OnPropertyChanged(nameof(IsSpinBlocked));
            OnPropertyChanged(nameof(CanAcceptPick));
            OnPropertyChanged(nameof(CanResetProtection));
            ScheduleCooldownCheck();
            UpdateWinnerProtection(Winner);
        }

        private string DescribeProtection(RerollProtectionOptions options)
        {
            if (!options.Enabled || selection.BlockReason == SpinBlockReason.EmptyWheel)
            {
                return null;
            }

            if (selection.IsBlocked)
            {
                return selection.Message;
            }

            var parts = NextSpinNotes();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        private List<string> NextSpinNotes()
        {
            var parts = new List<string>();
            if (selection.RerollsRemaining.HasValue && selection.SessionActive)
            {
                parts.Add(RandomiserContext.Plural(selection.RerollsRemaining.Value, "reroll") + " left");
            }

            if (selection.ExcludedIds.Count > 0)
            {
                parts.Add(RandomiserContext.Plural(selection.ExcludedIds.Count, "recent winner") + " skipped next spin");
            }

            if (selection.ExclusionBypassed)
            {
                parts.Add(selection.Message);
            }

            return parts;
        }

        private void UpdateWinnerProtection(WinnerViewModel model)
        {
            if (model == null)
            {
                return;
            }

            var options = context.Settings.Reroll;
            if (!options.Enabled || ActiveWheel?.Id != spinningWheelId)
            {
                model.ProtectionText = null;
                model.CanSpinAgain = true;
                model.CanAccept = false;
                model.CanResetProtection = false;
                return;
            }

            var lines = new List<string>();
            if (model.EligibleCount.HasValue)
            {
                lines.Add("Picked from " + RandomiserContext.Plural(model.EligibleCount.Value, "eligible game"));
            }

            if (IsSpinBlocked)
            {
                lines.Add(selection.Message);
            }
            else
            {
                lines.AddRange(NextSpinNotes());
            }

            model.ProtectionText = lines.Count == 0 ? null : string.Join("\n", lines);
            model.CanSpinAgain = !selection.IsBlocked;
            model.CanAccept = options.LimitRerolls && selection.SessionActive;
            model.CanResetProtection = IsSpinBlocked;
        }

        /// <summary>While cooling down, re-check when the cooldown ends (and periodically, to keep the minutes current).</summary>
        private void ScheduleCooldownCheck()
        {
            cooldownTimer?.Stop();
            if (selection.BlockReason != SpinBlockReason.CoolingDown || !selection.CooldownUntilUtc.HasValue)
            {
                return;
            }

            var remaining = selection.CooldownUntilUtc.Value - DateTime.UtcNow;
            var wait = TimeSpan.FromSeconds(Math.Max(0.25, Math.Min(30, remaining.TotalSeconds + 0.25)));
            if (cooldownTimer == null)
            {
                cooldownTimer = new DispatcherTimer();
                cooldownTimer.Tick += (s, e) =>
                {
                    cooldownTimer.Stop();
                    RefreshProtection();
                };
            }

            cooldownTimer.Interval = wait;
            cooldownTimer.Start();
        }

        private void RefreshManage()
        {
            var checkedIds = new HashSet<Guid>(manageGames.Where(g => g.IsChecked).Select(g => g.Id));
            var wheel = ActiveWheel;
            var pinned = wheel != null && wheel.MembershipPolicy == MembershipPolicy.CriteriaPlusPinned
                ? new HashSet<Guid>(wheel.PinnedGameIds)
                : new HashSet<Guid>();
            manageGames.Clear();
            foreach (var game in entries.OrderBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase))
            {
                manageGames.Add(new ManageGameItem(game, pinned.Contains(game.Id)) { IsChecked = checkedIds.Contains(game.Id) });
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
            cooldownTimer?.Stop();
            spinSuspension?.Dispose();
            spinSuspension = null;
            context.Wheels.Changed -= OnWheelsChanged;
            context.SettingsChanged -= OnSettingsChanged;
            context.LibraryChanged -= OnLibraryChanged;
        }
    }
}
