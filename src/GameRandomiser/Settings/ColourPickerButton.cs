using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GameRandomiser.Core.Layout;
using GameRandomiser.Services;

namespace GameRandomiser.Settings
{
    /// <summary>
    /// A swatch button that opens a small palette + hex entry popup. Built in code so it has no
    /// XAML dependencies and inherits Playnite's control styles inside the settings page.
    /// </summary>
    public sealed class ColourPickerButton : UserControl
    {
        public static readonly DependencyProperty ColourProperty = DependencyProperty.Register(
            nameof(Colour), typeof(string), typeof(ColourPickerButton),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColourPickerButton)d).Refresh()));

        public static readonly DependencyProperty AllowThemeDefaultProperty = DependencyProperty.Register(
            nameof(AllowThemeDefault), typeof(bool), typeof(ColourPickerButton), new PropertyMetadata(false, (d, e) => ((ColourPickerButton)d).Refresh()));

        private static readonly string[] Presets =
        {
            "#D50F25", "#E91E63", "#D63384", "#8E44AD", "#673AB7", "#3F51B5", "#3369E8", "#1E88E5",
            "#03A9F4", "#16A5A5", "#009688", "#009925", "#4CAF50", "#8BC34A", "#CDDC39", "#EEB211",
            "#FFC107", "#F26B1D", "#FF5722", "#795548", "#9E9E9E", "#607D8B", "#263238", "#FFFFFF"
        };

        private readonly Border swatch = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        private readonly TextBlock label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 };
        private readonly Popup popup = new Popup { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };
        private readonly TextBox hexBox = new TextBox { Width = 100, Margin = new Thickness(0, 0, 6, 0) };

        public ColourPickerButton()
        {
            var button = new Button { Padding = new Thickness(6, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, label } };
            button.Click += (s, e) =>
            {
                hexBox.Text = Colour;
                popup.IsOpen = true;
            };
            popup.PlacementTarget = button;
            popup.Child = BuildPopup();
            Content = new Grid { Children = { button, popup } };
            Refresh();
        }

        public string Colour
        {
            get => (string)GetValue(ColourProperty);
            set => SetValue(ColourProperty, value);
        }

        /// <summary>When true, an empty value means "use the theme's accent" and a reset button is offered.</summary>
        public bool AllowThemeDefault
        {
            get => (bool)GetValue(AllowThemeDefaultProperty);
            set => SetValue(AllowThemeDefaultProperty, value);
        }

        private UIElement BuildPopup()
        {
            var grid = new UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var preset in Presets)
            {
                var colour = ThemeService.Hex(preset);
                var b = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2),
                    Padding = new Thickness(0),
                    ToolTip = preset,
                    Content = new Border { Background = new SolidColorBrush(colour), CornerRadius = new CornerRadius(3), Width = 20, Height = 20 }
                };
                b.Click += (s, e) => Choose(preset);
                grid.Children.Add(b);
            }

            var apply = new Button { Content = "Apply", Padding = new Thickness(10, 3, 10, 3) };
            apply.Click += (s, e) =>
            {
                if (SegmentPalette.TryParseHex(hexBox.Text, out var r, out var g, out var bl))
                {
                    Choose(SegmentPalette.ToHex(r, g, bl));
                }
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { hexBox, apply } };
            var panel = new StackPanel { Children = { grid, row } };
            // Only shown for pickers where "empty" is meaningful (the accent colour).
            var reset = new Button { Content = "Use theme accent", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            reset.Click += (s, e) => Choose(string.Empty);
            reset.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(AllowThemeDefault))
            {
                Source = this,
                Converter = new BooleanToVisibilityConverter()
            });
            panel.Children.Add(reset);

            var border = new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = panel
            };
            border.SetResourceReference(Border.BackgroundProperty, "PopupBackgroundBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "PopupBorderBrush");
            return border;
        }

        private void Choose(string hex)
        {
            Colour = hex;
            popup.IsOpen = false;
        }

        private void Refresh()
        {
            if (!string.IsNullOrWhiteSpace(Colour) && SegmentPalette.TryParseHex(Colour, out var r, out var g, out var b))
            {
                swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                label.Text = SegmentPalette.ToHex(r, g, b);
            }
            else
            {
                swatch.Background = new LinearGradientBrush(Colors.LightGray, Colors.DimGray, 45);
                label.Text = AllowThemeDefault ? "Theme accent" : "(none)";
            }
        }
    }
}
