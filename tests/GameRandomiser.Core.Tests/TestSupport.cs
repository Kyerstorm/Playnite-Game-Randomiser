using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
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

        /// <summary>Simulates a library that cannot be read.</summary>
        public bool FailReads { get; set; }

        public IReadOnlyList<GameInfo> GetAllGames() =>
            FailReads ? throw new InvalidOperationException("The library is unavailable.") : games.Values.ToList();

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

    /// <summary>A store whose writes can be made to fail, to exercise save-failure recovery.</summary>
    internal sealed class FlakyStore : IRandomiserStore
    {
        public bool Fail { get; set; }
        public int SaveCount { get; private set; }
        public int Attempts { get; private set; }
        public RandomiserData Saved { get; private set; }

        public StoreLoadResult Load() => new StoreLoadResult(new RandomiserData());

        public void Save(RandomiserData data)
        {
            Attempts++;
            if (Fail)
            {
                throw new IOException("The disk is full.");
            }

            Saved = data;
            SaveCount++;
        }
    }

    internal sealed class RecordingReporter : IErrorReporter
    {
        public List<RandomiserError> Errors { get; } = new List<RandomiserError>();

        public void Report(RandomiserError error) => Errors.Add(error);
    }

    /// <summary>
    /// Deterministic stand-in for the dispatcher/thread-pool scheduler. Debounced callbacks and
    /// background work only run when the test says so, which makes ordering scenarios repeatable.
    /// </summary>
    internal sealed class ManualScheduler : IRefreshScheduler
    {
        private readonly Queue<Action> work = new Queue<Action>();
        private Action debounced;

        /// <summary>When true, background work waits for <see cref="CompleteNext"/> instead of running inline.</summary>
        public bool HoldWork { get; set; }

        public int DebounceCalls { get; private set; }
        public bool Disposed { get; private set; }
        public int PendingWork => work.Count;
        public bool HasDebounced => debounced != null;

        public void Debounce(Action callback)
        {
            DebounceCalls++;
            debounced = callback;
        }

        /// <summary>The debounce interval elapsed. Returns false if nothing was waiting.</summary>
        public bool FireDebounce()
        {
            var callback = debounced;
            debounced = null;
            callback?.Invoke();
            return callback != null;
        }

        public void Run<T>(Func<T> job, Action<T, Exception> completed)
        {
            Action run = () =>
            {
                var result = default(T);
                Exception error = null;
                try
                {
                    result = job();
                }
                catch (Exception e)
                {
                    error = e;
                }

                completed(result, error);
            };

            if (HoldWork)
            {
                work.Enqueue(run);
            }
            else
            {
                run();
            }
        }

        public void CompleteNext() => work.Dequeue()();

        public void Dispose() => Disposed = true;
    }

    /// <summary>A catalog that hands the coordinator a caller-supplied snapshot function.</summary>
    internal sealed class SnapshotCatalog : IGameCatalog, ILibrarySnapshotSource
    {
        private readonly IGameCatalog inner;

        public SnapshotCatalog(IGameCatalog inner) => this.inner = inner;

        public Func<IGameCatalog> Snapshot { get; set; }

        public Func<IGameCatalog> PrepareSnapshot(bool includeFilteredView) =>
            Snapshot ?? (() => LibrarySnapshot.Capture(inner, includeFilteredView));

        public GameInfo TryGet(Guid gameId) => inner.TryGet(gameId);

        public IReadOnlyList<GameInfo> GetAllGames() => inner.GetAllGames();

        public IReadOnlyList<GameInfo> GetFilteredGames() => inner.GetFilteredGames();

        public IReadOnlyList<NamedItem> GetLookup(LookupKind kind) => inner.GetLookup(kind);
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
