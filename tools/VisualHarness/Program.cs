using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameRandomiser;
using GameRandomiser.Core.Animation;
using GameRandomiser.Core.Models;
using GameRandomiser.UI;
using Moq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRandomiser.VisualHarness
{
    /// <summary>
    /// Renders the real Randomiser view against a mocked Playnite API and writes PNG screenshots,
    /// so layout, label fitting, themes and the spin -> winner flow can be checked without Playnite.
    /// Usage: GameRandomiser.VisualHarness.exe [output-folder]
    /// </summary>
    public static class Program
    {
        private static string outputDir;
        private static readonly List<string> Log = new List<string>();

        private static readonly string[] Names =
        {
            "Hades", "Baldur's Gate 3", "Hollow Knight", "Cyberpunk 2077", "Elden Ring", "Celeste", "Stardew Valley",
            "Marvel's Guardians of the Galaxy", "The Legend of Heroes: Trails in the Sky the 3rd", "Disco Elysium - The Final Cut",
            "Outer Wilds", "Portal 2", "Returnal", "Sekiro: Shadows Die Twice", "The Witcher 3: Wild Hunt", "Dead Cells",
            "Slay the Spire", "Red Dead Redemption 2", "Persona 5 Royal", "Death Stranding Director's Cut", "Inscryption",
            "Tunic", "Control Ultimate Edition", "Ori and the Will of the Wisps", "Pentiment", "Hi-Fi Rush", "Balatro",
            "Metroid Dread", "Final Fantasy VII Rebirth", "Star Wars Jedi: Survivor", "A Short Hike", "Cocoon", "Lies of P",
            "Alan Wake 2", "Pizza Tower", "Sea of Stars", "Dave the Diver", "Armored Core VI: Fires of Rubicon", "Hogwarts Legacy",
            "Horizon Forbidden West Complete Edition", "It Takes Two", "Deep Rock Galactic", "Vampire Survivors", "Terraria",
            "Factorio", "Rimworld", "Subnautica", "No Man's Sky"
        };

        [STAThread]
        public static int Main(string[] args)
        {
            outputDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "GameRandomiserShots");
            Directory.CreateDirectory(outputDir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var exitCode = 0;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Log.Add("FAILED: " + e);
                exitCode = 1;
            }

            File.WriteAllLines(Path.Combine(outputDir, "harness-log.txt"), Log);
            Console.WriteLine(string.Join(Environment.NewLine, Log));
            app.Shutdown();
            return exitCode;
        }

        private static void Run()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "GameRandomiserHarness", Guid.NewGuid().ToString("N"));
            var coverDir = Path.Combine(dataRoot, "covers");
            Directory.CreateDirectory(coverDir);

            var games = new List<Game>();
            var random = new Random(4);
            for (var i = 0; i < 260; i++)
            {
                var name = i < Names.Length ? Names[i] : $"Indie Gem #{i:000}";
                var game = new Game(name) { IsInstalled = i % 3 != 0, Playtime = (ulong)(i % 4 == 0 ? 0 : random.Next(1, 200) * 3600) };
                if (i < 40 && i % 7 != 5)
                {
                    game.CoverImage = MakeCover(coverDir, i, name);
                }

                if (i < 40 && i % 5 != 4)
                {
                    game.Icon = MakeIcon(coverDir, i);
                }

                games.Add(game);
            }

            var hidden = new Game("Secret Hidden Game") { Hidden = true };
            games.Add(hidden);

            // Surface silent WPF binding failures (typos in binding paths etc.).
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            var api = CreateApi(games, dataRoot);
            var plugin = new GameRandomiserPlugin(api);

            // 1. First run
            var window = CreateWindow(460, 720);
            var view = Show(window, plugin);
            Shoot(window, "01-first-run-narrow");
            Close(window, view);

            plugin.OnApplicationStarted(new Playnite.SDK.Events.OnApplicationStartedEventArgs());
            var ctx = plugin.Context;
            var visible = games.Where(g => !g.Hidden).ToList();
            var backlog = ctx.Wheels.CreateWheel("Backlog", visible.Take(24).Select(g => g.Id).Concat(new[] { hidden.Id }));
            Check("hidden game excluded from wheel", !backlog.GameIds.Contains(hidden.Id));
            var rpgs = ctx.Wheels.CreateWheel("RPGs", visible.Skip(1).Take(9).Select(g => g.Id), icon: "⭐");
            var covers = ctx.Wheels.CreateWheel("Covers", visible.Take(12).Select(g => g.Id), icon: "🎮");
            var huge = ctx.Wheels.CreateWheel("Everything", visible.Select(g => g.Id), icon: "📚");
            var single = ctx.Wheels.CreateWheel("Just one", new[] { visible[0].Id }, icon: "🎯");
            var empty = ctx.Wheels.CreateWheel("Empty wheel", null, icon: "👻");
            ctx.Wheels.SetActiveWheel(backlog.Id);

            // 2. Text wheel, several widths
            foreach (var size in new[] { (1400, 860, "02-wheel-wide"), (900, 780, "03-wheel-medium"), (380, 640, "04-wheel-narrow"), (300, 420, "05-wheel-tiny") })
            {
                window = CreateWindow(size.Item1, size.Item2);
                view = Show(window, plugin);
                Shoot(window, size.Item3);
                Close(window, view);
            }

            // 3. Spin and verify the winner lands under the pointer.
            window = CreateWindow(1100, 820);
            view = Show(window, plugin);
            for (var spin = 0; spin < 3; spin++)
            {
                Click(view.SpinButton);
                Pump(TimeSpan.FromMilliseconds(1600));
                if (spin == 0)
                {
                    Shoot(window, "06-mid-spin");
                }

                Pump(TimeSpan.FromSeconds(ctx.Settings.SpinDurationSeconds));
                var winnerId = ctx.History.GetHistory(backlog.Id).First().GameId;
                var entries = ctx.Wheels.ResolveEntries(backlog.Id);
                var underPointer = entries[WheelGeometry.IndexUnderPointer(view.Wheel.Rotation, entries.Count)];
                Check($"spin {spin + 1}: winner '{underPointer.Name}' is under the pointer", underPointer.Id == winnerId);
                Check($"spin {spin + 1}: winner card visible", view.WinnerOverlay.Visibility == Visibility.Visible);
                if (spin == 0)
                {
                    Shoot(window, "07-winner-card");
                }

                ((SidebarViewModel)view.DataContext).HideWinner();
                Pump(TimeSpan.FromMilliseconds(50));
            }

            Check("history recorded 3 spins", ctx.History.GetHistory(backlog.Id).Count == 3);
            Check("wheel still has all games (no auto-removal by default)", ctx.Wheels.GetWheel(backlog.Id).GameIds.Count == 24);
            Close(window, view);

            window = CreateWindow(1400, 860);
            view = Show(window, plugin);
            Shoot(window, "08-wheel-wide-with-recent-picks");
            Close(window, view);

            // 4. Display modes
            ctx.Wheels.SetActiveWheel(covers.Id);
            foreach (var mode in new[] { GameDisplayMode.Cover, GameDisplayMode.SmallCoverAndTitle, GameDisplayMode.IconAndTitle })
            {
                ctx.Settings.DisplayMode = mode;
                ctx.OnSettingsSaved();
                window = CreateWindow(900, 800);
                view = Show(window, plugin);
                Pump(TimeSpan.FromMilliseconds(900)); // background image decode + redraw
                Shoot(window, "09-mode-" + mode);
                Close(window, view);
            }

            ctx.Settings.DisplayMode = GameDisplayMode.Text;
            ctx.OnSettingsSaved();

            // 5. Few, many and single entries
            foreach (var wheel in new[] { (rpgs, "10-rpgs-9"), (huge, "11-large-259"), (single, "12-single"), (empty, "13-empty") })
            {
                ctx.Wheels.SetActiveWheel(wheel.Item1.Id);
                window = CreateWindow(900, 800);
                view = Show(window, plugin);
                Shoot(window, wheel.Item2);
                if (wheel.Item1 == single)
                {
                    Click(view.SpinButton);
                    Pump(TimeSpan.FromSeconds(2.2));
                    Check("single-game wheel spins quickly and wins", view.WinnerOverlay.Visibility == Visibility.Visible
                        && ctx.History.GetHistory(single.Id).FirstOrDefault()?.GameId == visible[0].Id);
                }

                if (wheel.Item1 == huge)
                {
                    Click(view.SpinButton);
                    Pump(TimeSpan.FromSeconds(ctx.Settings.SpinDurationSeconds + 1.2));
                    var entries = ctx.Wheels.ResolveEntries(huge.Id);
                    var winnerId = ctx.History.GetHistory(huge.Id).First().GameId;
                    Check("large wheel: winner under pointer", entries[WheelGeometry.IndexUnderPointer(view.Wheel.Rotation, entries.Count)].Id == winnerId);
                }

                Close(window, view);
            }

            // 6. Light theme, compact winner, auto remove
            ctx.Wheels.SetActiveWheel(rpgs.Id);
            ctx.Settings.Theme = ThemeMode.Light;
            ctx.Settings.WinnerPresentation = WinnerPresentation.Compact;
            ctx.Settings.WinnerBehaviour = WinnerBehaviour.RemoveAutomatically;
            ctx.Settings.SpinDurationSeconds = 2;
            ctx.OnSettingsSaved();
            window = CreateWindow(900, 800, Colors.White);
            view = Show(window, plugin);
            Shoot(window, "14-light-theme");
            Click(view.SpinButton);
            Pump(TimeSpan.FromSeconds(3.2));
            Shoot(window, "15-light-compact-winner-auto-removed");
            Check("auto-remove removed the winner", ctx.Wheels.GetWheel(rpgs.Id).GameIds.Count == 8);
            Close(window, view);

            ctx.Settings.Theme = ThemeMode.Dark;
            ctx.Settings.WinnerPresentation = WinnerPresentation.Card;
            ctx.Settings.WinnerBehaviour = WinnerBehaviour.AskMe;
            ctx.OnSettingsSaved();

            // 7. Manage + history tabs
            ctx.Wheels.SetActiveWheel(backlog.Id);
            foreach (var size in new[] { (1200, 800, "16-manage-wide"), (420, 760, "17-manage-narrow") })
            {
                window = CreateWindow(size.Item1, size.Item2);
                view = Show(window, plugin);
                view.SelectTab(SidebarTab.Manage);
                Pump(TimeSpan.FromMilliseconds(300));
                Shoot(window, size.Item3);
                Close(window, view);
            }

            window = CreateWindow(900, 760);
            view = Show(window, plugin);
            view.SelectTab(SidebarTab.History);
            Pump(TimeSpan.FromMilliseconds(600));
            Shoot(window, "18-history");
            Close(window, view);

            // 8. Dialogs: create wheel (automatic population), game picker, settings page.
            OnDialog<WheelCreationWindow>((w, view2) =>
            {
                view2.AutoRadio.IsChecked = true;
                Pump(TimeSpan.FromMilliseconds(400));
                Shoot(w, "19-create-wheel-dialog");
                Check("create dialog previews a game count", view2.CountText.Text.StartsWith("Games found:"));
                view2.CreateButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            });
            var wheelsBefore = ctx.Wheels.Wheels.Count;
            var created = ctx.CreateWheelInteractive();
            Check("create dialog created an automatic 'Never played' wheel",
                created != null && ctx.Wheels.Wheels.Count == wheelsBefore + 1 && created.Population?.Source == PopulationSource.NeverPlayed && created.GameIds.Count > 0);

            OnDialog<GamePickerWindow>((w, picker) =>
            {
                picker.SearchBox.Text = "the";
                Pump(TimeSpan.FromMilliseconds(500));
                Shoot(w, "20-game-picker");
                w.Close();
            });
            GamePickerWindow.ShowDialog(ctx, "Add games to Backlog", new HashSet<Guid>(ctx.Wheels.GetWheel(backlog.Id).GameIds));

            window = CreateWindow(820, 1400);
            var settingsVm = (GameRandomiser.Settings.RandomiserSettingsViewModel)plugin.GetSettings(false);
            settingsVm.BeginEdit();
            window.Content = plugin.GetSettingsView(false);
            window.Show();
            Pump(TimeSpan.FromMilliseconds(500));
            Shoot(window, "21-settings");
            settingsVm.CancelEdit();
            window.Close();

            // 9. Persistence: a brand-new plugin instance reads everything back.
            var reloaded = new GameRandomiserPlugin(api);
            Check("persistence: wheels restored", reloaded.Context.Wheels.Wheels.Count == ctx.Wheels.Wheels.Count);
            Check("persistence: history restored", reloaded.Context.History.GetHistory(backlog.Id).Count == 3);
            Check("persistence: active wheel restored", reloaded.Context.Wheels.ActiveWheel.Id == ctx.Wheels.ActiveWheel.Id);

            // 9. Library changes: deleting and hiding games cleans wheels.
            var toDelete = games.First(g => backlog.GameIds.Contains(g.Id));
            games.Remove(toDelete);
            ctx.Wheels.RemoveGamesFromAllWheels(new[] { toDelete.Id }); // what the ItemCollectionChanged handler does
            Check("deleted game removed from wheels", ctx.Wheels.WheelsContaining(toDelete.Id).Count == 0);
            var toHide = games.First(g => ctx.Wheels.GetWheel(backlog.Id).GameIds.Contains(g.Id));
            toHide.Hidden = true;
            Check("hidden game not resolved", ctx.Wheels.ResolveEntries(backlog.Id).All(g => g.Id != toHide.Id));
            Check("cleanup removes hidden", ctx.Wheels.CleanupInvalidEntries() >= 1);

            Log.Add("Screenshots written to " + outputDir);
        }

        // ---------- helpers ----------

        private static void Check(string what, bool ok) => Log.Add((ok ? "PASS  " : "FAIL  ") + what);

        /// <summary>Runs <paramref name="interact"/> against the next modal dialog hosting a <typeparamref name="T"/>.</summary>
        private static void OnDialog<T>(Action<Window, T> interact) where T : FrameworkElement
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (s, e) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.Content is T);
                if (dialog == null)
                {
                    return; // not shown yet; try again next tick
                }

                timer.Stop();
                try
                {
                    interact(dialog, (T)dialog.Content);
                }
                catch (Exception ex)
                {
                    Log.Add("FAIL  dialog interaction: " + ex.Message);
                    dialog.Close();
                }
            };
            timer.Start();
        }

        private sealed class BindingErrorListener : System.Diagnostics.TraceListener
        {
            public override void Write(string message)
            {
            }

            public override void WriteLine(string message) => Log.Add("BINDING ERROR  " + message);
        }

        private static Window CreateWindow(int width, int height, Color? background = null) => new Window
        {
            Width = width,
            Height = height,
            Left = -30000,
            Top = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(background ?? Color.FromRgb(0x1B, 0x1D, 0x22)),
            FontFamily = new FontFamily("Segoe UI")
        };

        private static SidebarView Show(Window window, GameRandomiserPlugin plugin)
        {
            var view = new SidebarView(plugin.Context);
            window.Content = view;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            return view;
        }

        private static void Close(Window window, SidebarView view)
        {
            view.Detach();
            window.Close();
        }

        private static void Click(System.Windows.Controls.Button button)
        {
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Pump(TimeSpan.FromMilliseconds(30));
        }

        private static void Pump(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = duration };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Shoot(Window window, string name)
        {
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }

            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(outputDir, name + ".png")))
            {
                encoder.Save(stream);
            }
        }

        private static string MakeCover(string dir, int index, string title)
        {
            var hue = index * 47 % 360;
            var top = FromHsv(hue, 0.65, 0.85);
            var bottom = FromHsv((hue + 40) % 360, 0.8, 0.35);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, 300, 450));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), null, new Point(220, 110), 90, 90);
                var text = new FormattedText(title.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Black"), 34, Brushes.White, 1.0) { MaxTextWidth = 260 };
                dc.DrawText(text, new Point(20, 430 - text.Height));
            }

            return Save(dir, $"cover{index}.png", visual, 300, 450);
        }

        private static string MakeIcon(string dir, int index)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(FromHsv(index * 83 % 360, 0.7, 0.9)), null, new Rect(0, 0, 64, 64), 14, 14);
                dc.DrawEllipse(Brushes.White, null, new Point(32, 32), 14, 14);
            }

            return Save(dir, $"icon{index}.png", visual, 64, 64);
        }

        private static string Save(string dir, string file, Visual visual, int w, int h)
        {
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(dir, file);
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            return path;
        }

        private static Color FromHsv(double h, double s, double v)
        {
            var c = v * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private static IPlayniteAPI CreateApi(List<Game> games, string dataRoot)
        {
            var gamesCollection = new Mock<IItemCollection<Game>>();
            gamesCollection.Setup(c => c.Get(It.IsAny<Guid>())).Returns((Guid id) => games.FirstOrDefault(g => g.Id == id));
            gamesCollection.Setup(c => c.GetEnumerator()).Returns(() => games.ToList().GetEnumerator());
            gamesCollection.Setup(c => c.Count).Returns(() => games.Count);
            gamesCollection.Setup(c => c.CopyTo(It.IsAny<Game[]>(), It.IsAny<int>())).Callback((Game[] a, int i) => games.CopyTo(a, i));

            IItemCollection<T> Empty<T>() where T : DatabaseObject
            {
                var mock = new Mock<IItemCollection<T>>();
                mock.Setup(c => c.GetEnumerator()).Returns(() => new List<T>().GetEnumerator());
                return mock.Object;
            }

            var database = new Mock<IGameDatabaseAPI>();
            database.Setup(d => d.Games).Returns(gamesCollection.Object);
            database.Setup(d => d.Genres).Returns(Empty<Genre>());
            database.Setup(d => d.Platforms).Returns(Empty<Platform>());
            database.Setup(d => d.Tags).Returns(Empty<Tag>());
            database.Setup(d => d.Categories).Returns(Empty<Category>());
            database.Setup(d => d.CompletionStatuses).Returns(Empty<CompletionStatus>());
            database.Setup(d => d.GetFullFilePath(It.IsAny<string>())).Returns((string p) => p);

            var paths = new Mock<IPlaynitePathsAPI>();
            paths.Setup(p => p.ExtensionsDataPath).Returns(dataRoot);
            paths.Setup(p => p.ApplicationPath).Returns(dataRoot);
            paths.Setup(p => p.ConfigurationPath).Returns(dataRoot);

            var mainView = new Mock<IMainViewAPI>();
            mainView.Setup(m => m.FilteredGames).Returns(() => games.Where(g => !g.Hidden).ToList());
            mainView.Setup(m => m.SortOrder).Returns(SortOrder.Name);
            mainView.Setup(m => m.SortOrderDirection).Returns(SortOrderDirection.Ascending);

            var info = new Mock<IPlayniteInfoAPI>();
            info.Setup(i => i.Mode).Returns(ApplicationMode.Desktop);

            var dialogs = new Mock<IDialogsFactory>();
            dialogs.Setup(d => d.CreateWindow(It.IsAny<WindowCreationOptions>())).Returns(() =>
            {
                var dialog = new Window { ShowInTaskbar = false, ShowActivated = false, Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x22)), Foreground = Brushes.White };
                // Keep harness dialogs off-screen even though the code under test centres them.
                dialog.SourceInitialized += (s, e) =>
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -30000;
                };
                dialog.Loaded += (s, e) => dialog.Left = -30000;
                return dialog;
            });
            dialogs.Setup(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageBoxButton>(), It.IsAny<MessageBoxImage>()))
                .Returns(MessageBoxResult.Yes)
                .Callback((string m, string c, MessageBoxButton b, MessageBoxImage i) => Log.Add("dialog: " + m.Replace("\n", " ")));
            dialogs.Setup(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>()))
                .Callback((string m, string c) => Log.Add("dialog: " + m.Replace("\n", " ")));

            var api = new Mock<IPlayniteAPI> { DefaultValue = DefaultValue.Mock };
            api.Setup(a => a.Dialogs).Returns(dialogs.Object);
            api.Setup(a => a.Database).Returns(database.Object);
            api.Setup(a => a.Paths).Returns(paths.Object);
            api.Setup(a => a.MainView).Returns(mainView.Object);
            api.Setup(a => a.ApplicationInfo).Returns(info.Object);
            return api.Object;
        }
    }
}
