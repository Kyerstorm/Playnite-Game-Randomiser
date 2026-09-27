using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Layout;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Persistence;
using GameRandomiser.Core.Services;

namespace GameRandomiser.Core.Tests
{
    internal sealed class FakeCatalog : IGameCatalog
    {
        private readonly Dictionary<Guid, GameInfo> games = new Dictionary<Guid, GameInfo>();

        public List<GameInfo> Filtered { get; } = new List<GameInfo>();
        public Dictionary<LookupKind, List<NamedItem>> Lookups { get; } = new Dictionary<LookupKind, List<NamedItem>>();

        public GameInfo Add(string name, Action<GameInfo> configure = null)
        {
            var game = new GameInfo { Id = Guid.NewGuid(), Name = name, LibraryIndex = games.Count };
            configure?.Invoke(game);
            games[game.Id] = game;
            return game;
        }

        public void Remove(Guid id) => games.Remove(id);

        public GameInfo TryGet(Guid gameId) => games.TryGetValue(gameId, out var g) ? g : null;

        public IReadOnlyList<GameInfo> GetAllGames() => games.Values.ToList();

        public IReadOnlyList<GameInfo> GetFilteredGames() => Filtered;

        public IReadOnlyList<NamedItem> GetLookup(LookupKind kind) =>
            Lookups.TryGetValue(kind, out var list) ? list : new List<NamedItem>();
    }

    internal sealed class InMemoryStore : IRandomiserStore
    {
        public RandomiserData Saved { get; private set; } = new RandomiserData();
        public int SaveCount { get; private set; }

        public StoreLoadResult Load() => new StoreLoadResult(Saved);

        public void Save(RandomiserData data)
        {
            Saved = data;
            SaveCount++;
        }
    }

    internal sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Deterministic monospace-ish text measurer for layout tests.</summary>
    internal sealed class FakeMeasurer : ITextMeasurer
    {
        public double MeasureWidth(string text, double fontSize) => (text ?? string.Empty).Length * fontSize * 0.55;

        public double LineHeight(double fontSize) => fontSize * 1.25;
    }

    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameRandomiserTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static class Build
    {
        public static WheelService Service(FakeCatalog catalog, IRandomiserStore store = null, int seed = 1, IClock clock = null) =>
            new WheelService(store ?? new InMemoryStore(), catalog, new SeededRandomSource(seed), clock ?? new FixedClock());

        public static JsonFileStore FileStore(string path) => new JsonFileStore(path, DataMigrator.CreateDefault());
    }
}
