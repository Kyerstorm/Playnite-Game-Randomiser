using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GameRandomiser.Services;
using Playnite.SDK;

namespace GameRandomiser.UI
{
    /// <summary>Searchable, virtualised multi-select list of library games.</summary>
    public partial class GamePickerWindow : UserControl
    {
        private readonly List<PickItem> items;
        private readonly ICollectionView view;
        private readonly DispatcherTimer searchTimer;
        private Window window;
        private List<Guid> result;

        private GamePickerWindow(RandomiserContext context, ISet<Guid> alreadyOnWheel, IEnumerable<Guid> preselected)
        {
            InitializeComponent();
            var selected = new HashSet<Guid>(preselected ?? Enumerable.Empty<Guid>());
            items = context.Catalog.GetAllGames()
                .Where(g => !g.IsHidden)
                .OrderBy(g => g.SortKey, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new PickItem(g.Id, g.Name, g.IsInstalled, alreadyOnWheel.Contains(g.Id)) { IsChecked = selected.Contains(g.Id) })
                .ToList();

            GameList.ItemsSource = items;
            view = CollectionViewSource.GetDefaultView(items);
            view.Filter = Matches;

            FilterCombo.ItemsSource = new[] { "All games", "Installed", "Not installed" };
            FilterCombo.SelectedIndex = 0;

            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            searchTimer.Tick += (s, e) =>
            {
                searchTimer.Stop();
                view.Refresh();
            };
            UpdateSelectedText();
        }

        public static List<Guid> ShowDialog(RandomiserContext context, string title, ISet<Guid> alreadyOnWheel, IEnumerable<Guid> preselected = null)
        {
            var picker = new GamePickerWindow(context, alreadyOnWheel ?? new HashSet<Guid>(), preselected);
            var window = context.Api.Dialogs.CreateWindow(new WindowCreationOptions { ShowCloseButton = true, ShowMaximizeButton = true, ShowMinimizeButton = false });
            picker.window = window;
            window.Title = title;
            window.Width = 620;
            window.Height = 720;
            window.Content = picker;
            window.Owner = context.Api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Loaded += (s, e) => picker.SearchBox.Focus();
            window.ShowDialog();
            return picker.result;
        }

        private bool Matches(object o)
        {
            var item = (PickItem)o;
            var search = SearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(search) && item.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                return false;
            }

            switch (FilterCombo.SelectedIndex)
            {
                case 1: return item.IsInstalled;
                case 2: return !item.IsInstalled;
                default: return true;
            }
        }

        private void UpdateSelectedText()
        {
            var count = items.Count(i => i.IsChecked && i.CanSelect);
            SelectedText.Text = count == 0 ? string.Empty : RandomiserContext.Plural(count, "game") + " selected";
            ConfirmButton.Content = count == 0 ? "Done" : $"Add {RandomiserContext.Plural(count, "game")}";
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchTimer?.Stop();
            searchTimer?.Start();
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e) => view?.Refresh();

        private void Item_Changed(object sender, RoutedEventArgs e) => UpdateSelectedText();

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (PickItem item in view)
            {
                if (item.CanSelect)
                {
                    item.IsChecked = true;
                }
            }

            UpdateSelectedText();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in items)
            {
                item.IsChecked = false;
            }

            UpdateSelectedText();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            result = items.Where(i => i.IsChecked && i.CanSelect).Select(i => i.Id).ToList();
            window?.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            result = null;
            window?.Close();
        }

        private sealed class PickItem : INotifyPropertyChanged
        {
            private bool isChecked;

            public PickItem(Guid id, string name, bool isInstalled, bool alreadyOnWheel)
            {
                Id = id;
                Name = name;
                IsInstalled = isInstalled;
                AlreadyOnWheel = alreadyOnWheel;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            public Guid Id { get; }
            public string Name { get; }
            public bool IsInstalled { get; }
            public bool AlreadyOnWheel { get; }
            public bool CanSelect => !AlreadyOnWheel;
            public string StatusText => AlreadyOnWheel ? "Already on wheel" : IsInstalled ? "Installed" : "Not installed";

            public bool IsChecked
            {
                get => isChecked || AlreadyOnWheel;
                set
                {
                    if (AlreadyOnWheel || isChecked == value)
                    {
                        return;
                    }

                    isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
    }
}
