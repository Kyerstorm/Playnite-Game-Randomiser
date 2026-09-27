using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GameRandomiser.Core.Layout;
using GameRandomiser.Core.Models;
using GameRandomiser.Settings;

namespace GameRandomiser.Services
{
    /// <summary>Resolved colours for one render of the UI.</summary>
    public sealed class ThemePalette
    {
        public Color Background { get; set; }
        public Color Surface { get; set; }
        public Color SurfaceAlt { get; set; }
        public Color Text { get; set; }
        public Color TextMuted { get; set; }
        public Color Border { get; set; }
        public Color Accent { get; set; }
        public Color AccentText { get; set; }
        public bool TransparentBackground { get; set; }
        public IReadOnlyList<Color> SegmentColours { get; set; }
    }

    /// <summary>
    /// Maps the Light / Dark / Native theme setting to concrete colours and publishes them as
    /// "GR.*" brush resources on a view. Native mode reads Playnite's own theme resources
    /// (TextBrush, PopupBackgroundBrush, GlyphBrush, ...) so the extension matches any installed theme.
    /// </summary>
    public static class ThemeService
    {
        public const string DefaultAccentHex = "#1E88E5";

        public static ThemePalette Resolve(RandomiserSettings settings)
        {
            ThemePalette palette;
            switch (settings.Theme)
            {
                case ThemeMode.Light:
                    palette = new ThemePalette
                    {
                        Background = Hex("#F4F5F8"),
                        Surface = Hex("#FFFFFF"),
                        SurfaceAlt = Hex("#E8EBF0"),
                        Text = Hex("#1B1F24"),
                        TextMuted = Hex("#5A636E"),
                        Border = Hex("#D3D8E0"),
                        Accent = Hex(DefaultAccentHex)
                    };
                    break;
                case ThemeMode.Dark:
                    palette = new ThemePalette
                    {
                        Background = Hex("#15171B"),
                        Surface = Hex("#1F2228"),
                        SurfaceAlt = Hex("#2B2F37"),
                        Text = Hex("#F1F3F5"),
                        TextMuted = Hex("#A0A8B2"),
                        Border = Hex("#383D48"),
                        Accent = Hex(DefaultAccentHex)
                    };
                    break;
                default:
                    palette = new ThemePalette
                    {
                        TransparentBackground = true,
                        Background = FromResource("WindowBackgourndBrush", "#1B1D22"),
                        Surface = FromResource("PopupBackgroundBrush", "#24272E"),
                        SurfaceAlt = FromResource("ButtonBackgroundBrush", FromResource("NormalBrush", "#31353E")),
                        Text = FromResource("TextBrush", "#F0F0F0"),
                        TextMuted = FromResource("TextBrushDarker", "#A6A6A6"),
                        Border = FromResource("PanelSeparatorBrush", "#3C414C"),
                        Accent = FromResource("GlyphBrush", DefaultAccentHex)
                    };
                    break;
            }

            if (!string.IsNullOrWhiteSpace(settings.AccentColour) && SegmentPalette.TryParseHex(settings.AccentColour, out var r, out var g, out var b))
            {
                palette.Accent = Color.FromRgb(r, g, b);
            }

            palette.AccentText = SegmentPalette.PrefersDarkText(palette.Accent.R, palette.Accent.G, palette.Accent.B)
                ? Hex("#111111")
                : Colors.White;

            var segmentColours = (settings.SegmentColours ?? new List<string>())
                .Select(c => SegmentPalette.TryParseHex(c, out var sr, out var sg, out var sb) ? Color.FromRgb(sr, sg, sb) : (Color?)null)
                .Where(c => c.HasValue)
                .Select(c => c.Value)
                .ToList();
            palette.SegmentColours = segmentColours.Count > 0
                ? segmentColours
                : SegmentPalette.DefaultColours.Select(Hex).ToList();
            return palette;
        }

        public static void Apply(FrameworkElement root, ThemePalette palette)
        {
            var resources = root.Resources;
            resources["GR.Background"] = palette.TransparentBackground ? Brushes.Transparent : Frozen(palette.Background);
            resources["GR.SolidBackground"] = Frozen(palette.Background);
            resources["GR.Surface"] = Frozen(palette.Surface);
            resources["GR.SurfaceAlt"] = Frozen(palette.SurfaceAlt);
            resources["GR.Text"] = Frozen(palette.Text);
            resources["GR.TextMuted"] = Frozen(palette.TextMuted);
            resources["GR.Border"] = Frozen(palette.Border);
            resources["GR.Accent"] = Frozen(palette.Accent);
            resources["GR.AccentText"] = Frozen(palette.AccentText);
            resources["GR.AccentSoft"] = Frozen(Color.FromArgb(0x33, palette.Accent.R, palette.Accent.G, palette.Accent.B));
            resources["GR.Hover"] = Frozen(Color.FromArgb(0x22, palette.Text.R, palette.Text.G, palette.Text.B));
            resources["GR.Overlay"] = Frozen(Color.FromArgb(0xB0, 0, 0, 0));
            resources["GR.Success"] = Frozen(Hex("#2E9E57"));
            resources["GR.Warning"] = Frozen(Hex("#D9822B"));
        }

        public static Color Hex(string hex) =>
            SegmentPalette.TryParseHex(hex, out var r, out var g, out var b) ? Color.FromRgb(r, g, b) : Colors.Gray;

        public static string ToHex(Color c) => SegmentPalette.ToHex(c.R, c.G, c.B);

        private static SolidColorBrush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        private static Color FromResource(string key, string fallbackHex) => FromResource(key, Hex(fallbackHex));

        private static Color FromResource(string key, Color fallback)
        {
            var resource = Application.Current?.TryFindResource(key);
            switch (resource)
            {
                case SolidColorBrush solid:
                    return solid.Color;
                case GradientBrush gradient when gradient.GradientStops.Count > 0:
                    return gradient.GradientStops[0].Color;
                case Color color:
                    return color;
                default:
                    return fallback;
            }
        }
    }
}
