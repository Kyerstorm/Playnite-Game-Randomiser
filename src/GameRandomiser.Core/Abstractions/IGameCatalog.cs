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
