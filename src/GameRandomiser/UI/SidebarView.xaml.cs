using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameRandomiser.Core.Animation;
using GameRandomiser.Services;

namespace GameRandomiser.UI
{
    /// <summary>
    /// The Randomiser view shown in Playnite's sidebar (and in the standalone window).
    /// Code-behind only wires the wheel render surface, animations and responsive layout;
    /// behaviour lives in <see cref="SidebarViewModel"/>.
    /// </summary>
    public partial class SidebarView : UserControl
    {
        private const double WideLayoutWidth = 1000;
        private const double TwoColumnManageWidth = 820;

        private static readonly string[] WheelIcons = { "🎲", "🎮", "⭐", "👥", "🎯", "🗡", "🏁", "🧩", "👻", "🚀", "🏆", "📚", "❤", "🔥" };

        private readonly RandomiserContext context;
        private readonly SidebarViewModel viewModel;
        private bool detached;

        public SidebarView(RandomiserContext context)
        {
            InitializeComponent();
            this.context = context;
            viewModel = new SidebarViewModel(context);
            DataContext = viewModel;

            ApplyTheme();
            Wheel.SetImageCache(context.Images);
            Wheel.SetAppearance(viewModel.BuildAppearance());
            Wheel.SetEntries(viewModel.Entries);

            viewModel.EntriesChanged += (s, e) => Wheel.SetEntries(viewModel.Entries);
            viewModel.AppearanceChanged += (s, e) =>
            {
                ApplyTheme();
                Wheel.SetAppearance(viewModel.BuildAppearance());
                UpdateResponsiveLayout();
            };
            viewModel.WinnerRevealed += (s, e) => ShowWinner();
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Wheel.Tick += (s, index) => context.Audio.PlayTick();
            Wheel.SpinCompleted += (s, plan) => viewModel.CompleteSpin(plan);
            Wheel.SpinRequested += (s, e) => StartSpin();

            SizeChanged += (s, e) => UpdateResponsiveLayout();
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (s, e) =>
            {
                UpdateResponsiveLayout();
                if (!viewModel.IsSpinning)
                {
                    // Pick up library changes that happened while the view was closed.
                    viewModel.RefreshAll();
                }
            };
        }

        public void SelectTab(SidebarTab tab) => viewModel.Tab = tab;

        /// <summary>Called when the hosting sidebar/window closes. Finishes any spin so its result is recorded.</summary>
        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            Wheel.FinishImmediately();
            Confetti.Stop();
            viewModel.Dispose();
        }

        private void ApplyTheme() => ThemeService.Apply(this, viewModel.Palette);

        private void StartSpin()
        {
            Guard(() =>
            {
                if (Wheel.IsSpinning)
                {
                    return;
                }

                var plan = viewModel.BeginSpin(Wheel.Rotation);
                if (plan != null)
                {
                    HideWinnerOverlay();
                    Confetti.Stop();
                    Wheel.ClearHighlight();
                    Wheel.Spin(plan);
                }
            });
        }

        private void ShowWinner()
        {
            var compact = viewModel.IsCompactWinner;
            WinnerCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            WinnerBanner.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            WinnerBackdrop.Opacity = compact ? 0.0 : 1.0;
            WinnerBackdrop.IsHitTestVisible = !compact;
            WinnerOverlay.IsHitTestVisible = true;
            WinnerOverlay.Visibility = Visibility.Visible;
            UpdateResponsiveLayout();

            // Scale + fade in with a little overshoot.
            var scale = compact ? WinnerBannerScale : WinnerCardScale;
            var target = compact ? (UIElement)WinnerBanner : WinnerCard;
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(320);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, duration) { EasingFunction = ease });
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            if (!compact)
            {
                WinnerBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            }

            if (context.Settings.ShowConfetti)
            {
                var origin = Wheel.TranslatePoint(new Point(Wheel.ActualWidth / 2, Wheel.ActualHeight * 0.45), Confetti);
                Confetti.Burst(viewModel.Palette.SegmentColours, origin);
            }

            // Move keyboard focus into the winner actions for accessibility.
            Dispatcher.BeginInvoke(new Action(() => (compact ? (UIElement)WinnerBanner : WinnerCard).MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
        }

        private void HideWinnerOverlay()
        {
            WinnerOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SidebarViewModel.IsWinnerVisible) && !viewModel.IsWinnerVisible)
            {
                HideWinnerOverlay();
            }
            else if (e.PropertyName == nameof(SidebarViewModel.HasWheels) || e.PropertyName == nameof(SidebarViewModel.HasHistory))
            {
                UpdateResponsiveLayout();
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && WinnerOverlay.Visibility == Visibility.Visible)
            {
                viewModel.HideWinner();
                e.Handled = true;
            }
        }

        /// <summary>Adapts the layout to the space Playnite gives the view, from a narrow window to a full-screen main view.</summary>
        private void UpdateResponsiveLayout()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0)
            {
                return;
            }

            // Wide: show recent picks beside the wheel.
            var showRecent = width >= WideLayoutWidth && viewModel.HasWheels && context.Settings.ShowRecentPicksWhenWide;
            RecentColumn.Width = showRecent ? new GridLength(Math.Min(360, width * 0.26)) : new GridLength(0);
            RecentPanel.Visibility = showRecent ? Visibility.Visible : Visibility.Collapsed;

            // Manage tab: side-by-side when wide, stacked when narrow.
            var twoColumns = width >= TwoColumnManageWidth;
            ManageRightColumn.Width = twoColumns ? new GridLength(1.6, GridUnitType.Star) : new GridLength(0);
            ManageTopRow.Height = twoColumns ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            Grid.SetRow(GamesPanel, twoColumns ? 0 : 1);
            Grid.SetColumn(GamesPanel, twoColumns ? 1 : 0);
            Grid.SetRowSpan(WheelsPanel, twoColumns ? 2 : 1);
            Grid.SetRowSpan(GamesPanel, twoColumns ? 2 : 1);
            WheelsPanel.Margin = twoColumns ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 12);
            WheelsList.MaxHeight = twoColumns ? double.PositiveInfinity : 240;

            // Narrow: tighten header labels so the tabs keep fitting.
            HeaderTitle.Visibility = width < 520 ? Visibility.Collapsed : Visibility.Visible;
            ShuffleLabel.Visibility = width < 360 ? Visibility.Collapsed : Visibility.Visible;
            SpinButton.MinWidth = width < 360 ? 120 : 180;

            // Winner card scales with the available space and always stays reachable.
            WinnerCard.Width = Math.Max(260, Math.Min(400, width - 32));
            WinnerScroll.MaxHeight = Math.Max(200, height - 90);
            var smallHeight = height < 640;
            WinnerCoverFrame.Width = smallHeight ? 120 : 180;
            WinnerCoverFrame.Height = smallHeight ? 168 : 252;
        }

        private void Guard(Action action) => context.SafeRun("complete that action", action);

        // ---- Event handlers ----

        private void Spin_Click(object sender, RoutedEventArgs e) => StartSpin();

        private void SpinAgain_Click(object sender, RoutedEventArgs e)
        {
            viewModel.HideWinner();
            StartSpin();
        }

        private void Shuffle_Click(object sender, RoutedEventArgs e) => Guard(viewModel.Shuffle);

        private void Settings_Click(object sender, RoutedEventArgs e) => context.OpenSettings();

        private void CreateWheel_Click(object sender, RoutedEventArgs e) => Guard(viewModel.CreateWheel);

        private void AddGames_Click(object sender, RoutedEventArgs e) => Guard(viewModel.AddGames);

        private void Rename_Click(object sender, RoutedEventArgs e) => Guard(viewModel.RenameWheel);

        private void Duplicate_Click(object sender, RoutedEventArgs e) => Guard(viewModel.DuplicateWheel);

        private void Delete_Click(object sender, RoutedEventArgs e) => Guard(viewModel.DeleteWheel);

        private void Clear_Click(object sender, RoutedEventArgs e) => Guard(viewModel.ClearWheel);

        private void RefreshCriteria_Click(object sender, RoutedEventArgs e) => Guard(viewModel.RefreshFromCriteria);

        private void RemoveSelected_Click(object sender, RoutedEventArgs e) => Guard(viewModel.RemoveCheckedGames);

        private void SelectAll_Click(object sender, RoutedEventArgs e) => viewModel.SetAllChecked(true);

        private void SelectNone_Click(object sender, RoutedEventArgs e) => viewModel.SetAllChecked(false);

        private void BackToWheel_Click(object sender, RoutedEventArgs e) => viewModel.Tab = SidebarTab.Wheel;

        private void ShowHistory_Click(object sender, RoutedEventArgs e) => viewModel.Tab = SidebarTab.History;

        private void ClearHistory_Click(object sender, RoutedEventArgs e) => Guard(viewModel.ClearHistory);

        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
            Guard(() => viewModel.ShowHistoryItemDetails(HistoryList.SelectedItem as HistoryItem));

        private void Icon_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = IconButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var icon in WheelIcons)
            {
                var item = new MenuItem { Header = icon, FontSize = 16 };
                item.Click += (s, args) => Guard(() => viewModel.SetWheelIcon(icon));
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private void CloseWinner_Click(object sender, RoutedEventArgs e) => viewModel.HideWinner();

        private void Remove_Click(object sender, RoutedEventArgs e) => Guard(viewModel.RemoveWinner);

        private void UndoRemove_Click(object sender, RoutedEventArgs e) => Guard(viewModel.UndoRemoveWinner);

        private void Launch_Click(object sender, RoutedEventArgs e) => Guard(viewModel.LaunchWinner);

        private void Install_Click(object sender, RoutedEventArgs e) => Guard(viewModel.InstallWinner);

        private void Details_Click(object sender, RoutedEventArgs e) => Guard(viewModel.ViewWinnerDetails);

        private void AcceptPick_Click(object sender, RoutedEventArgs e) => Guard(viewModel.AcceptPick);

        private void ResetProtection_Click(object sender, RoutedEventArgs e) => Guard(viewModel.ResetProtection);

        private void PinSelected_Click(object sender, RoutedEventArgs e) => Guard(viewModel.PinCheckedGames);

        private void UnpinSelected_Click(object sender, RoutedEventArgs e) => Guard(viewModel.UnpinCheckedGames);

        private void RestoreRemoved_Click(object sender, RoutedEventArgs e) => Guard(viewModel.RestoreRemovedGames);
    }
}
