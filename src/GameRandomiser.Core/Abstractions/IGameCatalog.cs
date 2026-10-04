using System;
using System.Collections.Generic;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Abstractions
{
    /// <summary>
    /// Read-only access to the game library. The plugin implements this over Playnite's database;
    /// tests implement it in memory. This is the only way Core sees game metadata.
    /// </summary>
    public interface IGameCatalog
    {
        /// <summary>Returns the live game, or null if it no longer exists in the library.</summary>
        GameInfo TryGet(Guid gameId);

        /// <summary>Every game in the library, including hidden ones.</summary>
        IReadOnlyList<GameInfo> GetAllGames();

        /// <summary>Games currently visible in Playnite's filtered library view.</summary>
        IReadOnlyList<GameInfo> GetFilteredGames();

        IReadOnlyList<NamedItem> GetLookup(LookupKind kind);
    }

    /// <summary>
    /// An immutable copy of the library taken at one moment. Population rules are evaluated against
    /// a snapshot so filtering can run off the UI thread and sees one consistent library.
    /// </summary>
    public sealed class LibrarySnapshot : IGameCatalog
    {
        private static readonly IReadOnlyList<NamedItem> NoItems = new NamedItem[0];
        private readonly Dictionary<Guid, GameInfo> byId;
        private readonly IReadOnlyList<GameInfo> all;
        private readonly IReadOnlyList<GameInfo> filtered;

        public LibrarySnapshot(IEnumerable<GameInfo> allGames, IEnumerable<GameInfo> filteredGames = null)
        {
            var list = new List<GameInfo>();
            byId = new Dictionary<Guid, GameInfo>();
            foreach (var game in allGames ?? new GameInfo[0])
            {
                if (game != null && !byId.ContainsKey(game.Id))
                {
                    byId.Add(game.Id, game);
                    list.Add(game);
                }
            }

            all = list;
            filtered = filteredGames == null ? new GameInfo[0] : new List<GameInfo>(filteredGames);
        }

        /// <summary>Copies a live catalog. Call on the thread that owns the catalog.</summary>
        public static LibrarySnapshot Capture(IGameCatalog catalog, bool includeFilteredView) =>
            new LibrarySnapshot(catalog.GetAllGames(), includeFilteredView ? catalog.GetFilteredGames() : null);

        public GameInfo TryGet(Guid gameId) => byId.TryGetValue(gameId, out var game) ? game : null;

        public IReadOnlyList<GameInfo> GetAllGames() => all;

        public IReadOnlyList<GameInfo> GetFilteredGames() => filtered;

        public IReadOnlyList<NamedItem> GetLookup(LookupKind kind) => NoItems;
    }

    /// <summary>
    /// Implemented by catalogs that can be copied cheaply off the UI thread. Catalogs that don't
    /// implement it are copied synchronously with <see cref="LibrarySnapshot.Capture"/>.
    /// </summary>
    public interface ILibrarySnapshotSource
    {
        /// <summary>
        /// Called on the owning (UI) thread to capture anything thread-affine. The returned function
        /// builds the snapshot and may be invoked on any thread.
        /// </summary>
        Func<IGameCatalog> PrepareSnapshot(bool includeFilteredView);
    }

    public static class GameCatalogExtensions
    {
        /// <summary>Hidden or missing games are never eligible for a wheel.</summary>
        public static bool IsEligible(this IGameCatalog catalog, Guid gameId)
        {
            var game = catalog.TryGet(gameId);
            return game != null && !game.IsHidden;
        }
    }
}
