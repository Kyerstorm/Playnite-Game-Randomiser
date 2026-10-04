using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Population;
using GameRandomiser.Services;
using Playnite.SDK;

namespace GameRandomiser.UI
{
    /// <summary>"Create Randomiser Wheel": name, icon, and empty / manual / automatic population with a live match count.</summary>
    public partial class WheelCreationWindow : UserControl
    {
        private static readonly string[] Icons = { "🎲", "🎮", "⭐", "👥", "🎯", "🗡", "🏁", "🧩", "👻", "🚀", "🏆", "📚" };

        private readonly RandomiserContext context;
        private readonly ObservableCollection<LookupChoice> lookupItems = new ObservableCollection<LookupChoice>();
        private readonly DispatcherTimer previewTimer;
        private List<Guid> manualGames;
        private string icon = "🎲";
        private bool nameEdited;
        private bool settingName;
        private int previewCount;
        private Window window;

        private WheelCreationWindow(RandomiserContext context, List<Guid> preselected)
        {
            InitializeComponent();
            this.context = context;
            manualGames = preselected ?? new List<Guid>();
            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            previewTimer.Tick += (s, e) =>
            {
                previewTimer.Stop();
                UpdatePreview();
            };

            foreach (var glyph in Icons)
            {
                var button = new ToggleButton { Content = glyph, FontSize = 18, Width = 40, Height = 36, Margin = new Thickness(0, 0, 6, 6), ToolTip = "Wheel icon" };
                button.Click += (s, e) => SelectIcon(glyph);
                IconPanel.Children.Add(button);
            }

            SelectIcon(icon);

            RuleCombo.ItemsSource = context.Population.Registry.Rules;
            RuleCombo.SelectedItem = context.Population.Registry.Get(PopulationSource.NeverPlayed);
            LookupList.ItemsSource = lookupItems;
            CollectionViewSource.GetDefaultView(lookupItems).Filter = o =>
                string.IsNullOrWhiteSpace(LookupSearch.Text)
                || ((LookupChoice)o).Name.IndexOf(LookupSearch.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;

            var sortOptions = new[]
            {
                new Option<SortMode>(SortMode.Alphabetical, "A–Z"),
                new Option<SortMode>(SortMode.Random, "Random"),
                new Option<SortMode>(SortMode.Library, "Library order")
            };
            SortCombo.ItemsSource = sortOptions;
            SortCombo.SelectedItem = sortOptions.First(o => o.Value == context.Settings.DefaultSortMode);

            // Snapshot stays the default: a wheel only updates itself when the user asks for that.
            PolicyCombo.ItemsSource = MembershipPolicyOptions.All;
            PolicyCombo.SelectedItem = MembershipPolicyOptions.All[0];

            SetName(context.Wheels.MakeUniqueName(preselected != null && preselected.Count > 0 ? "My picks" : "New wheel"));
            if (manualGames.Count > 0)
            {
                ManualRadio.IsChecked = true;
            }
            else
            {
                EmptyRadio.IsChecked = true;
            }

            UpdateRulePanels();
            UpdateModePanels();
        }

        public RandomiserWheel Result { get; private set; }

        public static RandomiserWheel ShowDialog(RandomiserContext context, List<Guid> preselected)
        {
            var view = new WheelCreationWindow(context, preselected);
            var window = context.Api.Dialogs.CreateWindow(new WindowCreationOptions { ShowCloseButton = true, ShowMaximizeButton = false, ShowMinimizeButton = false });
            view.window = window;
            window.Title = "Create Randomiser Wheel";
            window.Width = 560;
            window.Height = 720;
            window.MinHeight = 420;
            window.Content = view;
            window.Owner = context.Api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Loaded += (s, e) =>
            {
                view.NameBox.Focus();
                view.NameBox.SelectAll();
            };
            window.ShowDialog();
            return view.Result;
        }

        private void SelectIcon(string glyph)
        {
            icon = glyph;
            foreach (ToggleButton button in IconPanel.Children)
            {
                button.IsChecked = (string)button.Content == glyph;
            }
        }

        private IPopulationRule SelectedRule => RuleCombo.SelectedItem as IPopulationRule;

        private PopulationSpec BuildSpec()
        {
            var rule = SelectedRule;
            if (rule == null)
            {
                return null;
            }

            var spec = new PopulationSpec { Source = rule.Source };
            if (rule.ParameterKind == RuleParameterKind.Lookup)
            {
                spec.ItemIds = lookupItems.Where(i => i.IsChecked).Select(i => i.Id).ToList();
            }
            else if (rule.ParameterKind == RuleParameterKind.Hours || rule.ParameterKind == RuleParameterKind.Days)
            {
                spec.Amount = double.TryParse(AmountBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var amount) && amount >= 0
                    ? amount
                    : rule.DefaultAmount;
            }

            spec.Description = context.Population.Describe(spec);
            return spec;
        }

        private void UpdateModePanels()
        {
            ManualPanel.IsEnabled = ManualRadio.IsChecked == true;
            AutoPanel.IsEnabled = AutoRadio.IsChecked == true;
            AutoPanel.Opacity = AutoPanel.IsEnabled ? 1 : 0.5;
            ManualCountText.Text = manualGames.Count == 0 ? "No games chosen yet" : RandomiserContext.Plural(manualGames.Count, "game") + " selected";
            SchedulePreview();
        }

        private void UpdateRulePanels()
        {
            var rule = SelectedRule;
            if (rule == null)
            {
                return;
            }

            RuleHelp.Text = rule.HelpText;
            LookupPanel.Visibility = rule.ParameterKind == RuleParameterKind.Lookup ? Visibility.Visible : Visibility.Collapsed;
            AmountPanel.Visibility = rule.ParameterKind == RuleParameterKind.Hours || rule.ParameterKind == RuleParameterKind.Days
                ? Visibility.Visible
                : Visibility.Collapsed;
            AmountUnit.Text = rule.ParameterKind == RuleParameterKind.Days ? "days" : "hours";
            AmountBox.Text = rule.DefaultAmount.ToString(CultureInfo.CurrentCulture);

            lookupItems.Clear();
            if (rule.ParameterKind == RuleParameterKind.Lookup && rule.LookupKind.HasValue)
            {
                LookupLabel.Text = rule.DisplayName;
                foreach (var item in context.Catalog.GetLookup(rule.LookupKind.Value))
                {
                    lookupItems.Add(new LookupChoice(item.Id, item.Name));
                }
            }

            LookupSearch.Text = string.Empty;
            SchedulePreview();
        }

        private void SchedulePreview()
        {
            previewTimer.Stop();
            previewTimer.Start();
        }

        private void UpdatePreview()
        {
            try
            {
                if (EmptyRadio.IsChecked == true)
                {
                    CountText.Text = "Empty wheel";
                    return;
                }

                if (ManualRadio.IsChecked == true)
                {
                    CountText.Text = "Games: " + manualGames.Count;
                    return;
                }

                var spec = BuildSpec();
                if (spec == null)
                {
                    return;
                }

                previewCount = context.Population.Evaluate(spec).Count;
                CountText.Text = "Games found: " + previewCount;
                if (!nameEdited && !string.IsNullOrEmpty(spec.Description))
                {
                    SetName(context.Wheels.MakeUniqueName(spec.Description));
                }
            }
            catch (Exception e)
            {
                CountText.Text = "Couldn't evaluate criteria";
                LogManager.GetLogger().Error(e, "Game Randomiser population preview failed.");
            }
        }

        private void SetName(string name)
        {
            settingName = true;
            NameBox.Text = name;
            settingName = false;
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!settingName)
            {
                nameEdited = true;
            }
        }

        private void Population_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded || ManualPanel != null)
            {
                UpdateModePanels();
            }
        }

        private void Criteria_Changed(object sender, EventArgs e)
        {
            if (ReferenceEquals(sender, RuleCombo))
            {
                UpdateRulePanels();
            }
            else
            {
                SchedulePreview();
            }
        }

        private void Lookup_Changed(object sender, RoutedEventArgs e) => SchedulePreview();

        private void Policy_Changed(object sender, SelectionChangedEventArgs e) =>
            PolicyHelp.Text = (PolicyCombo.SelectedItem as Option<MembershipPolicy>)?.Description ?? string.Empty;

        private void LookupSearch_TextChanged(object sender, TextChangedEventArgs e) =>
            CollectionViewSource.GetDefaultView(lookupItems).Refresh();

        private void ChooseGames_Click(object sender, RoutedEventArgs e)
        {
            var picked = GamePickerWindow.ShowDialog(context, "Choose games for the wheel", new HashSet<Guid>(), manualGames);
            if (picked != null)
            {
                manualGames = picked;
                UpdateModePanels();
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sort = (SortCombo.SelectedItem as Option<SortMode>)?.Value ?? SortMode.Alphabetical;
                IEnumerable<Guid> games = null;
                PopulationSpec spec = null;

                if (ManualRadio.IsChecked == true)
                {
                    games = manualGames;
                }
                else if (AutoRadio.IsChecked == true)
                {
                    spec = BuildSpec();
                    if (spec == null)
                    {
                        return;
                    }

                    if (SelectedRule.ParameterKind == RuleParameterKind.Lookup && spec.ItemIds.Count == 0)
                    {
                        context.Api.Dialogs.ShowMessage($"Tick at least one {SelectedRule.DisplayName.ToLowerInvariant()} to populate the wheel.", "Create wheel");
                        return;
                    }

                    var matches = context.Population.Evaluate(spec);
                    if (matches.Count == 0)
                    {
                        var answer = context.Api.Dialogs.ShowMessage("No games match these criteria. Create an empty wheel anyway?", "Create wheel",
                            MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    games = matches.Select(g => g.Id);
                }

                var policy = spec == null
                    ? MembershipPolicy.ManualSnapshot
                    : (PolicyCombo.SelectedItem as Option<MembershipPolicy>)?.Value ?? MembershipPolicy.ManualSnapshot;
                Result = context.Wheels.CreateWheel(NameBox.Text, games, sort, spec, icon, makeActive: true, policy: policy);
                window?.Close();
            }
            catch (ArgumentException ex)
            {
                context.Api.Dialogs.ShowMessage(ex.Message, "Create wheel");
                NameBox.Focus();
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Error(ex, "Game Randomiser failed to create a wheel.");
                context.Api.Dialogs.ShowErrorMessage("Couldn't create the wheel.\n\n" + ex.Message, "Create wheel");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => window?.Close();

        private sealed class LookupChoice : INotifyPropertyChanged
        {
            private bool isChecked;

            public LookupChoice(Guid id, string name)
            {
                Id = id;
                Name = name;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public Guid Id { get; }
            public string Name { get; }

            public bool IsChecked
            {
                get => isChecked;
                set
                {
                    isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
    }
}
