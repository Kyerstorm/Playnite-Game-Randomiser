using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Models;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameRandomiser.Integration
{
    /// <summary>
    /// Live, read-only view of the Playnite library for Core. Nothing here is cached except the
    /// library ordering index, which is invalidated whenever the library changes.
    /// </summary>
    public sealed class PlayniteGameCatalog : IGameCatalog, ILibrarySnapshotSource
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private Dictionary<Guid, int> libraryIndex;

        public PlayniteGameCatalog(IPlayniteAPI api)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public void InvalidateLibraryOrder() => libraryIndex = null;

        public Game GetPlayniteGame(Guid gameId)
        {
            try
            {
                return api.Database.Games.Get(gameId);
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to read game {gameId} from the Playnite database.");
                return null;
            }
        }

        public GameInfo TryGet(Guid gameId)
        {
            var game = GetPlayniteGame(gameId);
            return game == null ? null : Map(game);
        }

        public IReadOnlyList<GameInfo> GetAllGames() => api.Database.Games.Where(g => g != null).Select(Map).ToList();

        public IReadOnlyList<GameInfo> GetFilteredGames()
        {
            try
            {
                return (api.MainView.FilteredGames ?? new List<Game>()).Select(Map).ToList();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to read Playnite's filtered games.");
                return new GameInfo[0];
            }
        }

        public IReadOnlyList<NamedItem> GetLookup(LookupKind kind)
        {
            IEnumerable<DatabaseObject> items;
            switch (kind)
            {
                case LookupKind.Genre: items = api.Database.Genres; break;
                case LookupKind.Platform: items = api.Database.Platforms; break;
                case LookupKind.Tag: items = api.Database.Tags; break;
                case LookupKind.Category: items = api.Database.Categories; break;
                case LookupKind.CompletionStatus: items = api.Database.CompletionStatuses; break;
                default: items = Enumerable.Empty<DatabaseObject>(); break;
            }

            return items
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Name))
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new NamedItem(i.Id, i.Name))
                .ToList();
        }

        /// <summary>
        /// Captures what is only safe to read on the UI thread (the library view's filter and sort order),
        /// then returns a function that copies the database on any thread. The copy skips artwork paths,
        /// which cost two file-system checks per game and are never needed to evaluate criteria.
        /// </summary>
        public Func<IGameCatalog> PrepareSnapshot(bool includeFilteredView)
        {
            var filtered = includeFilteredView ? GetFilteredGames() : null;
            var index = libraryIndex ?? (libraryIndex = BuildLibraryIndex());
            return () => new LibrarySnapshot(
                api.Database.Games.Where(g => g != null).Select(g => Map(g, index, false)).ToList(),
                filtered);
        }

        private GameInfo Map(Game game) => Map(game, null, true);

        private GameInfo Map(Game game, Dictionary<Guid, int> index, bool resolveArtwork) => new GameInfo
        {
            Id = game.Id,
            Name = string.IsNullOrWhiteSpace(game.Name) ? "(Untitled game)" : game.Name,
            SortingName = game.SortingName,
            IsInstalled = game.IsInstalled,
            IsHidden = game.Hidden,
            CoverPath = resolveArtwork ? ResolveLocalFile(game.CoverImage) : null,
            IconPath = resolveArtwork ? ResolveLocalFile(game.Icon) : null,
            PlaytimeSeconds = game.Playtime,
            PlayCount = game.PlayCount,
            LastActivity = game.LastActivity,
            Added = game.Added,
            CompletionStatusId = game.CompletionStatusId,
            GenreIds = game.GenreIds ?? new List<Guid>(),
            PlatformIds = game.PlatformIds ?? new List<Guid>(),
            TagIds = game.TagIds ?? new List<Guid>(),
            CategoryIds = game.CategoryIds ?? new List<Guid>(),
            LibraryIndex = index == null
                ? GetLibraryIndex(game.Id)
                : index.TryGetValue(game.Id, out var position) ? position : int.MaxValue
        };

        /// <summary>Returns an absolute path for a database file, or null for missing/remote images.</summary>
        private string ResolveLocalFile(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var path = Path.IsPathRooted(value) ? value : api.Database.GetFullFilePath(value);
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private int GetLibraryIndex(Guid id)
        {
            if (libraryIndex == null)
            {
                libraryIndex = BuildLibraryIndex();
            }

            return libraryIndex.TryGetValue(id, out var index) ? index : int.MaxValue;
        }

        /// <summary>
        /// "Library" order mirrors how Playnite's main view is currently sorted, so the wheel matches
        /// what the user sees in their library. Falls back to date added for unsupported sort fields.
        /// </summary>
        private Dictionary<Guid, int> BuildLibraryIndex()
        {
            var games = api.Database.Games.Where(g => g != null).ToList();
            SortOrder order;
            SortOrderDirection direction;
            try
            {
                order = api.MainView.SortOrder;
                direction = api.MainView.SortOrderDirection;
            }
            catch (Exception)
            {
                order = SortOrder.Added;
                direction = SortOrderDirection.Descending;
            }

            Func<Game, IComparable> key;
            switch (order)
            {
                case SortOrder.Name: key = g => string.IsNullOrEmpty(g.SortingName) ? g.Name ?? string.Empty : g.SortingName; break;
                case SortOrder.LastActivity: key = g => g.LastActivity ?? DateTime.MinValue; break;
                case SortOrder.RecentActivity: key = g => g.RecentActivity ?? DateTime.MinValue; break;
                case SortOrder.ReleaseDate: key = g => g.ReleaseDate?.Date ?? DateTime.MinValue; break;
                case SortOrder.PlayCount: key = g => g.PlayCount; break;
                case SortOrder.Playtime: key = g => g.Playtime; break;
                case SortOrder.UserScore: key = g => g.UserScore ?? -1; break;
                case SortOrder.CriticScore: key = g => g.CriticScore ?? -1; break;
                case SortOrder.CommunityScore: key = g => g.CommunityScore ?? -1; break;
                case SortOrder.Modified: key = g => g.Modified ?? DateTime.MinValue; break;
                case SortOrder.IsInstalled: key = g => g.IsInstalled; break;
                case SortOrder.Favorite: key = g => g.Favorite; break;
                case SortOrder.InstallSize: key = g => g.InstallSize ?? 0; break;
                default: key = g => g.Added ?? DateTime.MinValue; break;
            }

            var sorted = direction == SortOrderDirection.Descending
                ? games.OrderByDescending(key).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                : games.OrderBy(key).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);

            var result = new Dictionary<Guid, int>(games.Count);
            var i = 0;
            foreach (var g in sorted)
            {
                result[g.Id] = i++;
            }

            return result;
        }
    }
}
