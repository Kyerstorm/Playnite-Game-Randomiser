using System;
using Playnite.SDK;

namespace GameRandomiser.Integration
{
    /// <summary>
    /// Winner actions routed through Playnite's native APIs so launches, installs and details behave
    /// exactly as if started from the library. Failures are reported, never thrown into the UI.
    /// </summary>
    public sealed class PlayniteGameActions
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly PlayniteGameCatalog catalog;

        public PlayniteGameActions(IPlayniteAPI api, PlayniteGameCatalog catalog)
        {
            this.api = api;
            this.catalog = catalog;
        }

        public bool CanLaunch(Guid gameId)
        {
            var game = catalog.GetPlayniteGame(gameId);
            return game != null && game.IsInstalled && !game.IsRunning && !game.IsLaunching;
        }

        public bool CanInstall(Guid gameId)
        {
            var game = catalog.GetPlayniteGame(gameId);
            return game != null && !game.IsInstalled && !game.IsInstalling;
        }

        public void Launch(Guid gameId) => Run(gameId, "launch", () => api.StartGame(gameId));

        public void Install(Guid gameId) => Run(gameId, "install", () => api.InstallGame(gameId));

        /// <summary>Opens Playnite's library view with the game selected (its standard details panel).</summary>
        public void ViewDetails(Guid gameId)
        {
            Run(gameId, "show", () =>
            {
                api.MainView.SwitchToLibraryView();
                api.MainView.SelectGame(gameId);
            });
        }

        private void Run(Guid gameId, string verb, Action action)
        {
            var game = catalog.GetPlayniteGame(gameId);
            if (game == null)
            {
                api.Dialogs.ShowErrorMessage("This game is no longer in your Playnite library.", "Game Randomiser");
                return;
            }

            try
            {
                action();
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to {verb} {game.Name}.");
                api.Dialogs.ShowErrorMessage($"Couldn't {verb} {game.Name}.\n\n{e.Message}", "Game Randomiser");
            }
        }
    }
}
