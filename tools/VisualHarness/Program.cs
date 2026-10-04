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
using GameRandomiser.Core.Services;
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

            // A failed check or a broken binding fails the run, so CI can gate on this tool.
            var problems = Log.Count(l => l.StartsWith("FAIL") || l.StartsWith("BINDING ERROR"));
            Log.Add($"{Log.Count(l => l.StartsWith("PASS"))} checks passed, {problems} problems.");
            if (problems > 0)
            {
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

            DynamicWheels(plugin, games);
            RerollProtection(plugin, rpgs, single);
            SaveFailure(plugin, api, dataRoot, backlog);
            Shutdown(plugin, games);
            LargeLibrary();

            Log.Add("Screenshots written to " + outputDir);
        }

        // ---------- reliability scenarios ----------

        /// <summary>10. Dynamic wheels follow the library through Playnite's own change events.</summary>
        private static void DynamicWheels(GameRandomiserPlugin plugin, List<Game> games)
        {
            var ctx = plugin.Context;
            var spec = new PopulationSpec { Source = PopulationSource.NeverPlayed };
            spec.Description = ctx.Population.Describe(spec);
            var matches = ctx.Population.Evaluate(spec).Select(g => g.Id).ToList();
            var snapshot = ctx.Wheels.CreateWheel("Unplayed (snapshot)", matches, SortMode.Alphabetical, spec, "📚", false);
            var strict = ctx.Wheels.CreateWheel("Unplayed (auto)", matches, SortMode.Alphabetical, spec, "🎯", true, MembershipPolicy.StrictCriteria);
            Pump(RefreshWait);
            Check("dynamic wheel: refreshed after creation", ctx.Wheels.GetRefreshInfo(strict.Id).Status == RefreshStatus.Refreshed);

            // A game gets played: it must leave the strict wheel, and a burst of events must cost one refresh.
            var played = games.First(g => strict.GameIds.Contains(g.Id));
            var before = Copy(played);
            played.Playtime = 7200;
            played.PlayCount = 1;
            played.LastActivity = DateTime.Now;
            var batches = ctx.Refresh.BatchesStarted;
            var writes = DataFileStamp(plugin);
            for (var i = 0; i < 40; i++)
            {
                RaiseUpdated(before, played);
            }

            Pump(RefreshWait);
            Check("dynamic wheel: played game left the strict wheel", !ctx.Wheels.GetWheel(strict.Id).GameIds.Contains(played.Id));
            Check("dynamic wheel: 40 library events were coalesced into one refresh", ctx.Refresh.BatchesStarted == batches + 1);
            Check("dynamic wheel: snapshot wheel was left alone", ctx.Wheels.GetWheel(snapshot.Id).GameIds.Contains(played.Id));
            Check("dynamic wheel: change was saved", DataFileStamp(plugin) != writes);

            // An unrelated change (cover art) must not trigger a refresh at all.
            var other = games.First(g => strict.GameIds.Contains(g.Id));
            var otherBefore = Copy(other);
            other.BackgroundImage = "changed.png";
            batches = ctx.Refresh.BatchesStarted;
            RaiseUpdated(otherBefore, other);
            Pump(RefreshWait);
            Check("dynamic wheel: unrelated metadata change does not refresh", ctx.Refresh.BatchesStarted == batches);

            // A new unplayed game arrives: the strict wheel gains it, the snapshot only on a manual refresh.
            var added = new Game("Brand New Unplayed Game") { IsInstalled = true };
            games.Add(added);
            GamesMock.Raise(c => c.ItemCollectionChanged += null, GamesMock.Object,
                new ItemCollectionChangedEventArgs<Game>(new List<Game> { added }, new List<Game>()));
            Pump(RefreshWait);
            Check("dynamic wheel: new matching game joined the strict wheel", ctx.Wheels.GetWheel(strict.Id).GameIds.Contains(added.Id));
            Check("dynamic wheel: snapshot wheel did not change by itself", !ctx.Wheels.GetWheel(snapshot.Id).GameIds.Contains(added.Id));

            ctx.RefreshWheelNow(ctx.Wheels.GetWheel(snapshot.Id));
            Pump(RefreshWait);
            Check("manual refresh: snapshot wheel gained the new game", ctx.Wheels.GetWheel(snapshot.Id).GameIds.Contains(added.Id));
            Check("manual refresh: kept games that no longer match", ctx.Wheels.GetWheel(snapshot.Id).GameIds.Contains(played.Id));
            Check("manual refresh: user was told the outcome", Log.Any(l => l.StartsWith("dialog:") && l.Contains("was refreshed")));

            // Criteria + pinned: a pinned game stays when it stops matching; a removed game stays removed.
            ctx.Wheels.SetMembershipPolicy(strict.Id, MembershipPolicy.CriteriaPlusPinned);
            Pump(RefreshWait);
            var current = ctx.Wheels.GetWheel(strict.Id);
            var pinned = games.First(g => current.GameIds.Contains(g.Id) && g.Id != added.Id);
            var removedByUser = games.First(g => current.GameIds.Contains(g.Id) && g.Id != added.Id && g.Id != pinned.Id);
            ctx.Wheels.PinGames(strict.Id, new[] { pinned.Id });
            ctx.Wheels.RemoveGames(strict.Id, new[] { removedByUser.Id });
            var pinnedBefore = Copy(pinned);
            pinned.Playtime = 3600;
            pinned.PlayCount = 1;
            RaiseUpdated(pinnedBefore, pinned);
            Pump(RefreshWait);
            current = ctx.Wheels.GetWheel(strict.Id);
            Check("pinned: pinned game stays after it stops matching", current.GameIds.Contains(pinned.Id));
            Check("pinned: a game the user removed does not come back", !current.GameIds.Contains(removedByUser.Id));

            var window = CreateWindow(1200, 800);
            var view = Show(window, plugin);
            var vm = (SidebarViewModel)view.DataContext;
            Check("dynamic wheel: wheel tab shows the auto badge", vm.DynamicBadgeText == "auto" && view.DynamicBadge.Visibility == Visibility.Visible);
            Shoot(window, "22-dynamic-wheel");
            view.SelectTab(SidebarTab.Manage);
            Pump(TimeSpan.FromMilliseconds(300));
            Check("dynamic wheel: refresh status is shown", view.RefreshStatus.Visibility == Visibility.Visible && view.RefreshStatus.Text.Contains("Up to date"));
            Check("dynamic wheel: removed games can be restored", vm.CanRestoreRemoved && vm.RemovedCount == 1);
            Shoot(window, "23-dynamic-manage");
            Close(window, view);

            // A refresh that fails keeps the last good list and says so.
            var good = ctx.Wheels.GetWheel(strict.Id).GameIds.ToList();
            FailLibraryReads = true;
            ctx.Refresh.InvalidateAll(RefreshReason.LibraryChanged);
            Pump(RefreshWait);
            FailLibraryReads = false;
            var info = ctx.Wheels.GetRefreshInfo(strict.Id);
            Check("failed refresh: last known good games are kept", ctx.Wheels.GetWheel(strict.Id).GameIds.SequenceEqual(good));
            Check("failed refresh: wheel is marked stale", info.Status == RefreshStatus.FailedUsingLastKnownGood && info.IsStale);
            Check("failed refresh: user was notified", Notifications.Any(n => n.Type == NotificationType.Error && n.Text.Contains("Unplayed (auto)")));
            Check("failed refresh: no stack trace in the message", Notifications.All(n => !n.Text.Contains("   at ")));
            window = CreateWindow(900, 800);
            view = Show(window, plugin);
            Shoot(window, "24-dynamic-stale");
            Close(window, view);

            ctx.Refresh.InvalidateAll(RefreshReason.LibraryChanged);
            Pump(RefreshWait);
            Check("failed refresh: recovers on the next refresh", ctx.Wheels.GetRefreshInfo(strict.Id).Status == RefreshStatus.Refreshed
                && Notifications.All(n => !n.Text.Contains("Unplayed (auto)")));
        }

        /// <summary>11. Reroll protection: exclusion, reroll limit, accept and the single-game bypass.</summary>
        private static void RerollProtection(GameRandomiserPlugin plugin, RandomiserWheel wheel, RandomiserWheel single)
        {
            var ctx = plugin.Context;
            ctx.Settings.WinnerBehaviour = WinnerBehaviour.KeepGame;
            ctx.Settings.SpinDurationSeconds = 2;
            ctx.Settings.Reroll.Enabled = true;
            ctx.Settings.Reroll.Exclusion = RerollExclusionMode.PreviousWinner;
            ctx.Settings.Reroll.LimitRerolls = true;
            ctx.Settings.Reroll.MaxRerolls = 2;
            ctx.OnSettingsSaved();
            ctx.Wheels.SetActiveWheel(wheel.Id);
            ctx.Reroll.Reset(wheel.Id);

            var window = CreateWindow(1000, 820);
            var view = Show(window, plugin);
            var vm = (SidebarViewModel)view.DataContext;
            var winners = new List<Guid>();
            var spins = 0;
            while (vm.CanSpin && spins < 6)
            {
                Click(view.SpinButton);
                Pump(TimeSpan.FromSeconds(3.2));
                winners.Add(ctx.History.GetHistory(wheel.Id).First().GameId);
                spins++;
                if (spins == 1)
                {
                    Check("reroll protection: winner card explains the next spin", !string.IsNullOrEmpty(vm.Winner?.ProtectionText));
                    Shoot(window, "25-reroll-winner-card");
                }

                if (vm.CanSpin)
                {
                    vm.HideWinner();
                    Pump(TimeSpan.FromMilliseconds(50));
                }
            }

            Check("reroll protection: 1 spin + 2 rerolls, then blocked", spins == 3 && !vm.CanSpin);
            Check("reroll protection: never the same game twice in a row", Enumerable.Range(1, winners.Count - 1).All(i => winners[i] != winners[i - 1]));
            Check("reroll protection: blocked state is explained", vm.IsSpinBlocked && !string.IsNullOrEmpty(vm.ProtectionStatusText)
                && vm.Winner != null && !vm.Winner.CanSpinAgain && vm.Winner.CanAccept);
            Check("reroll protection: spin button is disabled", !view.SpinButton.IsEnabled);
            Shoot(window, "26-reroll-limit-card");
            vm.HideWinner();
            Pump(TimeSpan.FromMilliseconds(400));
            Check("reroll protection: status line visible under the spin button", view.ProtectionBar.Visibility == Visibility.Visible);
            Shoot(window, "27-reroll-limit-status");

            var historyBefore = ctx.History.GetHistory(wheel.Id).Count;
            // The button is disabled; clicking the wheel itself goes through the same gate.
            Check("reroll protection: a blocked spin is refused", vm.BeginSpin(view.Wheel.Rotation) == null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check("reroll protection: a blocked spin does nothing", ctx.History.GetHistory(wheel.Id).Count == historyBefore && !vm.IsSpinning);

            vm.AcceptPick();
            Pump(TimeSpan.FromMilliseconds(100));
            Check("reroll protection: accepting the pick allows spinning again", vm.CanSpin);
            Close(window, view);

            // State is persisted per wheel and survives a restart when asked to.
            ctx.Settings.Reroll.ResetBehaviour = RerollResetBehaviour.KeepUntilReset;
            Check("reroll protection: recent winner is persisted", ctx.Wheels.GetWheel(wheel.Id).Reroll?.RecentWinnerIds.Count > 0);

            // A one-game wheel can always spin.
            single = ctx.Wheels.CreateWheel("Only one", new[] { ctx.Wheels.GetWheel(wheel.Id).GameIds[0] }, icon: "🎯");
            window = CreateWindow(900, 800);
            view = Show(window, plugin);
            vm = (SidebarViewModel)view.DataContext;
            var singleBefore = ctx.History.GetHistory(single.Id).Count;
            for (var i = 0; i < 2 && vm.CanSpin; i++)
            {
                Click(view.SpinButton);
                Pump(TimeSpan.FromSeconds(2.2));
                vm.HideWinner();
                Pump(TimeSpan.FromMilliseconds(50));
            }

            Check("reroll protection: single-game wheel still spins", ctx.History.GetHistory(single.Id).Count == singleBefore + 2);
            Close(window, view);

            // Settings page with the section enabled, light and dark.
            foreach (var theme in new[] { (ThemeMode.Dark, "28-settings-reroll"), (ThemeMode.Light, "29-settings-reroll-light") })
            {
                window = CreateWindow(820, 1700, theme.Item1 == ThemeMode.Light ? Colors.White : (Color?)null);
                var settingsVm = (GameRandomiser.Settings.RandomiserSettingsViewModel)plugin.GetSettings(false);
                settingsVm.BeginEdit();
                settingsVm.RerollEnabled = true;
                settingsVm.RecentWinnerCount = 4;
                window.Content = plugin.GetSettingsView(false);
                if (theme.Item1 == ThemeMode.Light)
                {
                    window.Foreground = Brushes.Black;
                }

                window.Show();
                Pump(TimeSpan.FromMilliseconds(500));
                Shoot(window, theme.Item2);
                // Saving goes through Playnite itself (not available here), so cancel and check the round trip.
                var edited = ctx.Settings.Reroll.RecentWinnerCount;
                settingsVm.CancelEdit();
                if (theme.Item1 == ThemeMode.Dark)
                {
                    Check("settings: reroll edits reach the settings and cancel restores them", edited == 4 && ctx.Settings.Reroll.RecentWinnerCount == 3);
                }

                window.Close();
            }

            ctx.Settings.Reroll.Enabled = false;
            ctx.Settings.WinnerBehaviour = WinnerBehaviour.AskMe;
            ctx.OnSettingsSaved();
            ctx.Wheels.SetActiveWheel(wheel.Id);
            window = CreateWindow(900, 800);
            view = Show(window, plugin);
            vm = (SidebarViewModel)view.DataContext;
            Check("reroll protection off: no status, spinning unrestricted", vm.CanSpin && !vm.HasProtectionStatus && view.ProtectionBar.Visibility != Visibility.Visible);
            Close(window, view);
        }

        /// <summary>12. A failed save keeps the change in memory, tells the user once and recovers.</summary>
        private static void SaveFailure(GameRandomiserPlugin plugin, IPlayniteAPI api, string dataRoot, RandomiserWheel wheel)
        {
            var ctx = plugin.Context;
            var dataFile = Directory.GetFiles(dataRoot, "data.json", SearchOption.AllDirectories).Single();
            var original = File.ReadAllText(dataFile);
            Notifications.Clear();
            using (new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ctx.Wheels.RenameWheel(wheel.Id, "Backlog (renamed)");
                ctx.Wheels.SetWheelIcon(wheel.Id, "🚀");
            }

            Check("save failure: change kept in memory", ctx.Wheels.GetWheel(wheel.Id).Name == "Backlog (renamed)" && ctx.Wheels.HasUnsavedChanges);
            Check("save failure: file on disk is intact", File.ReadAllText(dataFile) == original);
            Check("save failure: user notified exactly once", Notifications.Count(n => n.Type == NotificationType.Error) == 1);

            ctx.Wheels.SetWheelIcon(wheel.Id, "🎲");
            Check("save failure: next save succeeds and clears the warning", !ctx.Wheels.HasUnsavedChanges && Notifications.Count == 0);
            var reloaded = new GameRandomiserPlugin(api);
            Check("save failure: recovered data round-trips", reloaded.Context.Wheels.GetWheel(wheel.Id)?.Name == "Backlog (renamed)");
            Check("no temp files left behind", Directory.GetFiles(Path.GetDirectoryName(dataFile), "*.tmp").Length == 0);
        }

        /// <summary>13. After shutdown, late library events are ignored and nothing throws.</summary>
        private static void Shutdown(GameRandomiserPlugin plugin, List<Game> games)
        {
            var ctx = plugin.Context;
            var window = CreateWindow(900, 800);
            var view = Show(window, plugin);
            var game = games.First(g => !g.Hidden);
            var before = Copy(game);
            game.Playtime += 60;
            RaiseUpdated(before, game); // a refresh is now pending
            plugin.OnApplicationStopped(new Playnite.SDK.Events.OnApplicationStoppedEventArgs());
            var batches = ctx.Refresh.BatchesStarted;
            var stamp = DataFileStamp(plugin);
            RaiseUpdated(before, game);
            Pump(RefreshWait);
            Check("shutdown: pending and late refreshes are dropped", ctx.Refresh.BatchesStarted == batches && DataFileStamp(plugin) == stamp);
            Close(window, view);
            plugin.OnApplicationStopped(new Playnite.SDK.Events.OnApplicationStoppedEventArgs());
            Check("shutdown: disposing twice is harmless", true);
        }

        /// <summary>14. A 10,000-game library: refreshes must not stall the UI thread.</summary>
        private static void LargeLibrary()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "GameRandomiserHarness", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            var games = new List<Game>();
            for (var i = 0; i < 10000; i++)
            {
                games.Add(new Game($"Library Game {i:00000}")
                {
                    IsInstalled = i % 3 == 0,
                    Playtime = (ulong)(i % 4 == 0 ? 0 : 3600 * (i % 50 + 1)),
                    PlayCount = (ulong)(i % 4 == 0 ? 0 : 1)
                });
            }

            var plugin = new GameRandomiserPlugin(CreateApi(games, dataRoot));
            plugin.OnApplicationStarted(new Playnite.SDK.Events.OnApplicationStartedEventArgs());
            var ctx = plugin.Context;
            RandomiserWheel first = null;
            foreach (var source in new[] { PopulationSource.NeverPlayed, PopulationSource.Installed, PopulationSource.Played, PopulationSource.NotInstalled })
            {
                var spec = new PopulationSpec { Source = source };
                spec.Description = ctx.Population.Describe(spec);
                var wheel = ctx.Wheels.CreateWheel(spec.Description, null, SortMode.Alphabetical, spec, null, first == null, MembershipPolicy.StrictCriteria);
                first = first ?? wheel;
            }

            // Watch for UI-thread stalls while the four wheels are populated from 10,000 games.
            var longestGap = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
            heartbeat.Tick += (s, e) =>
            {
                var now = clock.Elapsed;
                if (now - last > longestGap)
                {
                    longestGap = now - last;
                }

                last = now;
            };
            heartbeat.Start();
            ctx.Refresh.InvalidateAll(RefreshReason.Startup); // what happens when Playnite starts with these wheels saved
            Pump(TimeSpan.FromSeconds(3));
            var populated = ctx.Wheels.Wheels.Sum(w => w.GameIds.Count);
            Check($"10k library: four dynamic wheels populated ({populated} entries)", ctx.Wheels.GetWheel(first.Id).GameIds.Count == 2500 && populated == 20000);
            Log.Add($"INFO  10k library: longest UI-thread gap while populating 4 wheels = {longestGap.TotalMilliseconds:0} ms");
            Budget("10k library: UI thread stayed responsive while populating (< 400 ms gap)", longestGap < TimeSpan.FromMilliseconds(400));

            // One game changes: only the wheels that read playtime are re-evaluated, and nothing stalls.
            longestGap = TimeSpan.Zero;
            last = clock.Elapsed;
            var game = games[0];
            var before = Copy(game);
            game.Playtime = 7200;
            game.PlayCount = 1;
            RaiseUpdated(before, game);
            Pump(RefreshWait);
            heartbeat.Stop();
            Check("10k library: single change reconciled", ctx.Wheels.GetWheel(first.Id).GameIds.Count == 2499);
            Log.Add($"INFO  10k library: longest UI-thread gap for a one-game change = {longestGap.TotalMilliseconds:0} ms");
            Budget("10k library: UI thread stayed responsive on change (< 250 ms gap)", longestGap < TimeSpan.FromMilliseconds(250));

            var timer = Stopwatch.StartNew();
            var window = CreateWindow(1000, 820);
            var view = Show(window, plugin);
            Log.Add($"INFO  10k library: showing a 2,499-game wheel took {timer.ElapsedMilliseconds - 400} ms (excluding the 400 ms settle)");
            Shoot(window, "30-large-library-wheel");
            Close(window, view);
            plugin.OnApplicationStopped(new Playnite.SDK.Events.OnApplicationStoppedEventArgs());
        }

        private static readonly TimeSpan RefreshWait = TimeSpan.FromMilliseconds(1500);
        private static readonly List<NotificationMessage> Notifications = new List<NotificationMessage>();
        private static Mock<IItemCollection<Game>> GamesMock;
        private static bool FailLibraryReads;

        private static Game Copy(Game game) => new Game(game.Name)
        {
            Id = game.Id,
            Hidden = game.Hidden,
            IsInstalled = game.IsInstalled,
            Playtime = game.Playtime,
            PlayCount = game.PlayCount,
            LastActivity = game.LastActivity,
            CoverImage = game.CoverImage,
            Icon = game.Icon,
            BackgroundImage = game.BackgroundImage
        };

        private static void RaiseUpdated(Game before, Game after) =>
            GamesMock.Raise(c => c.ItemUpdated += null, GamesMock.Object,
                new ItemUpdatedEventArgs<Game>(new List<ItemUpdateEvent<Game>> { new ItemUpdateEvent<Game>(before, after) }));

        /// <summary>Changes whenever the data file is rewritten.</summary>
        private static string DataFileStamp(GameRandomiserPlugin plugin)
        {
            var file = new FileInfo(Path.Combine(plugin.GetPluginUserDataPath(), "data.json"));
            return file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : "missing";
        }

        // ---------- helpers ----------

        private static void Check(string what, bool ok) => Log.Add((ok ? "PASS  " : "FAIL  ") + what);

        /// <summary>
        /// A timing budget. Shared CI runners are too noisy to gate on wall-clock time, so there a miss
        /// is reported as a warning; on a developer machine it fails the run.
        /// </summary>
        private static void Budget(string what, bool ok) =>
            Log.Add((ok ? "PASS  " : Environment.GetEnvironmentVariable("CI") == "true" ? "WARN  " : "FAIL  ") + what);

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
            gamesCollection.Setup(c => c.GetEnumerator()).Returns(() =>
            {
                if (FailLibraryReads)
                {
                    throw new InvalidOperationException("Simulated library failure.");
                }

                return games.ToList().GetEnumerator();
            });
            GamesMock = gamesCollection;

            var notifications = new Mock<INotificationsAPI>();
            notifications.Setup(n => n.Add(It.IsAny<NotificationMessage>())).Callback((NotificationMessage m) =>
            {
                Notifications.RemoveAll(x => x.Id == m.Id);
                Notifications.Add(m);
                Log.Add("notification: " + m.Text.Replace("\n", " "));
            });
            notifications.Setup(n => n.Remove(It.IsAny<string>())).Callback((string id) => Notifications.RemoveAll(x => x.Id == id));
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
            api.Setup(a => a.Notifications).Returns(notifications.Object);
            return api.Object;
        }
    }
}
