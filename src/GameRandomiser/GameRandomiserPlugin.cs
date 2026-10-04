using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameRandomiser.Integration;
using GameRandomiser.Services;
using GameRandomiser.Settings;
using GameRandomiser.UI;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace GameRandomiser
{
    /// <summary>
    /// Playnite entry point. Wires lifecycle events, the sidebar view, context/main menus and settings.
    /// All real work is delegated to <see cref="RandomiserContext"/> and the Core services.
    /// </summary>
    public class GameRandomiserPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly RandomiserSettingsViewModel settingsViewModel;
        private readonly ContextMenuBuilder contextMenu;
        private SidebarView sidebarView;

        public GameRandomiserPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            settingsViewModel = new RandomiserSettingsViewModel(this);
            Context = new RandomiserContext(this, api, settingsViewModel);
            contextMenu = new ContextMenuBuilder(Context);
        }

        public override Guid Id { get; } = Guid.Parse("5a1e7c3d-9b2f-4e68-a0c4-7d3b91f2e865");

        internal RandomiserContext Context { get; }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            try
            {
                Context.Initialize();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Game Randomiser failed to initialise.");
                Context.ReportStartupFailure(e);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            sidebarView?.Detach();
            Context.Dispose();
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            if (PlayniteApi.ApplicationInfo.Mode != ApplicationMode.Desktop)
            {
                yield break;
            }

            yield return new SidebarItem
            {
                Title = "Game Randomiser",
                Type = SiderbarItemType.View,
                Icon = CreateSidebarIcon(),
                Opened = () =>
                {
                    // A fresh view per opening keeps state in sync with anything that changed meanwhile.
                    sidebarView?.Detach();
                    sidebarView = new SidebarView(Context);
                    return sidebarView;
                },
                Closed = () =>
                {
                    sidebarView?.Detach();
                    sidebarView = null;
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            try
            {
                return contextMenu.Build(args.Games).ToList();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Game Randomiser failed to build its context menu.");
                return Enumerable.Empty<GameMenuItem>();
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            const string Section = "@Game Randomiser";
            yield return new MainMenuItem { Description = "Open Randomiser", MenuSection = Section, Action = _ => Context.OpenWindow() };
            yield return new MainMenuItem { Description = "Create wheel…", MenuSection = Section, Action = _ => Context.CreateWheelInteractive() };
            yield return new MainMenuItem { Description = "Manage wheels", MenuSection = Section, Action = _ => Context.OpenWindow(SidebarTab.Manage) };
            yield return new MainMenuItem { Description = "Settings", MenuSection = Section, Action = _ => Context.OpenSettings() };
        }

        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView { DataContext = settingsViewModel };

        /// <summary>A small wheel glyph drawn with the theme's text brush so it matches Playnite's other sidebar icons.</summary>
        private static FrameworkElement CreateSidebarIcon()
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var rim = new Ellipse { Width = 20, Height = 20, StrokeThickness = 2 };
            Canvas.SetLeft(rim, 2);
            Canvas.SetTop(rim, 2);
            rim.SetResourceReference(Shape.StrokeProperty, "TextBrush");
            var spokes = new Path
            {
                Data = Geometry.Parse("M12,4 L12,20 M4,12 L20,12 M6.4,6.4 L17.6,17.6 M17.6,6.4 L6.4,17.6"),
                StrokeThickness = 1.3
            };
            spokes.SetResourceReference(Shape.StrokeProperty, "TextBrush");
            var hub = new Ellipse { Width = 6, Height = 6 };
            Canvas.SetLeft(hub, 9);
            Canvas.SetTop(hub, 9);
            hub.SetResourceReference(Shape.FillProperty, "TextBrush");
            canvas.Children.Add(rim);
            canvas.Children.Add(spokes);
            canvas.Children.Add(hub);
            return new Viewbox { Child = canvas, Width = 24, Height = 24 };
        }
    }
}
