using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Abstractions;
using GameRandomiser.Core.Diagnostics;
using GameRandomiser.Core.Models;
using GameRandomiser.Core.Population;

namespace GameRandomiser.Core.Services
{
    /// <summary>
    /// How the coordinator defers and offloads work. The plugin implements this with a dispatcher
    /// timer and the thread pool; tests implement it synchronously.
    /// </summary>
    public interface IRefreshScheduler : IDisposable
    {
        /// <summary>
        /// Runs <paramref name="callback"/> on the owning thread once no further call arrives within
        /// the debounce interval. Each call replaces the previous one; the last request is never lost.
        /// </summary>
        void Debounce(Action callback);

        /// <summary>
        /// Runs <paramref name="work"/> away from the owning thread, then <paramref name="completed"/> on
        /// the owning thread with its result or the exception it threw.
        /// </summary>
        void Run<T>(Func<T> work, Action<T, Exception> completed);
    }

    public sealed class RefreshBatchEventArgs : EventArgs
    {
        public RefreshBatchEventArgs(IReadOnlyList<WheelRefreshResult> results) => Results = results;

        public IReadOnlyList<WheelRefreshResult> Results { get; }
    }

    /// <summary>
    /// The single place wheel refreshes go through. Requests are coalesced per wheel and debounced,
    /// only one refresh runs at a time, criteria are evaluated against a library snapshot off the
    /// owning thread, and results are applied in one save. While a spin is in progress results are
    /// held back and applied when it ends.
    /// Not thread-safe: every member except the scheduled work runs on the owning (UI) thread.
    /// </summary>
    public sealed class RefreshCoordinator : IDisposable
    {
        private readonly WheelService wheels;
        private readonly PopulationEngine engine;
        private readonly IGameCatalog catalog;
        private readonly IRefreshScheduler scheduler;
        private readonly IRandomSource random;
        private readonly IErrorReporter errors;
        private readonly Dictionary<Guid, Pending> pending = new Dictionary<Guid, Pending>();
        private List<RefreshOutcome> deferred;
        private int suspendCount;
        private int generation;
        private bool running;
        private bool disposed;

        public RefreshCoordinator(WheelService wheels, PopulationEngine engine, IGameCatalog catalog,
            IRefreshScheduler scheduler, IRandomSource random, IErrorReporter errors = null)
        {
            this.wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.errors = errors;
            wheels.Changed += OnWheelsChanged;
        }

        /// <summary>Raised on the owning thread after a batch of refreshes has been applied.</summary>
        public event EventHandler<RefreshBatchEventArgs> Completed;

        public bool IsRunning => running;

        public bool HasPending => pending.Count > 0 || deferred != null;

        public bool IsSuspended => suspendCount > 0;

        /// <summary>Number of refresh batches started. Lets tests prove that bursts are coalesced.</summary>
        public int BatchesStarted { get; private set; }

        /// <summary>
        /// The library changed in the given ways. Only dynamic wheels whose criteria read one of those
        /// aspects are queued.
        /// </summary>
        public void InvalidateLibrary(LibraryFields changed)
        {
            if (disposed || changed == LibraryFields.None)
            {
                return;
            }

            var any = false;
            foreach (var wheel in wheels.Wheels)
            {
                if (wheel.IsDynamic && (engine.DependenciesOf(wheel.Population) & changed) != 0)
                {
                    Enqueue(wheel.Id, RefreshReason.LibraryChanged, false);
                    any = true;
                }
            }

            if (any)
            {
                scheduler.Debounce(Flush);
            }
        }

        /// <summary>Queues every dynamic wheel (e.g. at startup).</summary>
        public void InvalidateAll(RefreshReason reason)
        {
            if (disposed)
            {
                return;
            }

            var any = false;
            foreach (var wheel in wheels.Wheels)
            {
                if (wheel.IsDynamic)
                {
                    Enqueue(wheel.Id, reason, false);
                    any = true;
                }
            }

            if (any)
            {
                scheduler.Debounce(Flush);
            }
        }

        /// <summary>
        /// Queues one wheel. A <see cref="RefreshReason.Manual"/> request skips the debounce and also
        /// works on snapshot wheels, where it adds newly matching games without removing any.
        /// </summary>
        public void InvalidateWheel(Guid wheelId, RefreshReason reason, bool pinUnmatched = false)
        {
            if (disposed)
            {
                return;
            }

            Enqueue(wheelId, reason, pinUnmatched);
            if (reason == RefreshReason.Manual)
            {
                Flush();
            }
            else
            {
                scheduler.Debounce(Flush);
            }
        }

        /// <summary>
        /// Holds back membership changes until the returned token is disposed. Used for the duration of
        /// a spin so the wheel being spun is never changed underneath the animation.
        /// </summary>
        public IDisposable Suspend()
        {
            suspendCount++;
            return new Suspension(this);
        }

        /// <summary>Starts refreshing everything queued, unless a refresh is running or a spin is in progress.</summary>
        public void Flush()
        {
            if (disposed || running || suspendCount > 0 || pending.Count == 0)
            {
                // Requests stay queued: the running refresh or the end of the spin picks them up.
                return;
            }

            var captures = new List<WheelCapture>();
            foreach (var request in pending.Values.ToList())
            {
                var wheel = wheels.GetWheel(request.WheelId);
                if (wheel == null || wheel.Population == null)
                {
                    continue;
                }

                if (!wheel.IsDynamic && request.Reason != RefreshReason.Manual)
                {
                    // Snapshot wheels are never changed automatically.
                    continue;
                }

                captures.Add(wheels.Capture(request.WheelId, request.Reason, request.PinUnmatched));
            }

            pending.Clear();
            if (captures.Count == 0)
            {
                return;
            }

            Func<IGameCatalog> snapshotWork;
            try
            {
                var needsFilteredView = captures.Any(c => (engine.DependenciesOf(c.Population) & LibraryFields.ViewFilter) != 0);
                snapshotWork = PrepareSnapshot(needsFilteredView);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                var failure = new LibraryUnavailableException("The library could not be read.", e);
                Apply(captures.Select(c => RefreshOutcome.Failure(c, failure)).ToList());
                return;
            }

            wheels.MarkRefreshing(captures.Select(c => c.WheelId));
            running = true;
            BatchesStarted++;
            var startedIn = generation;
            scheduler.Run(
                () => Compute(captures, snapshotWork),
                (outcomes, error) => OnComputed(captures, outcomes, error, startedIn));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            wheels.Changed -= OnWheelsChanged;
            pending.Clear();
            deferred = null;
            scheduler.Dispose();
        }

        private Func<IGameCatalog> PrepareSnapshot(bool includeFilteredView)
        {
            if (catalog is ILibrarySnapshotSource source)
            {
                return source.PrepareSnapshot(includeFilteredView);
            }

            var snapshot = LibrarySnapshot.Capture(catalog, includeFilteredView);
            return () => snapshot;
        }

        /// <summary>Runs away from the owning thread. Touches nothing but its arguments.</summary>
        private List<RefreshOutcome> Compute(List<WheelCapture> captures, Func<IGameCatalog> snapshotWork)
        {
            var library = snapshotWork();
            var libraryIsEmpty = library.GetAllGames().Count == 0;
            var outcomes = new List<RefreshOutcome>(captures.Count);
            foreach (var capture in captures)
            {
                try
                {
                    var problem = engine.Validate(capture.Population);
                    if (problem != null)
                    {
                        throw new InvalidPopulationRuleException(problem);
                    }

                    if (libraryIsEmpty && capture.GameIds.Count > 0)
                    {
                        // Deleted games are removed from wheels as they go, so a wheel that still has games
                        // while the library reports none means the library wasn't readable. Don't empty it.
                        throw new LibraryUnavailableException("The library returned no games.");
                    }

                    var matches = engine.Evaluate(capture.Population, library);
                    outcomes.Add(MembershipReconciler.Reconcile(capture, matches, library, random));
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    outcomes.Add(RefreshOutcome.Failure(capture, e));
                }
            }

            return outcomes;
        }

        private void OnComputed(List<WheelCapture> captures, List<RefreshOutcome> outcomes, Exception error, int startedIn)
        {
            running = false;
            if (disposed || startedIn != generation)
            {
                return;
            }

            if (error != null || outcomes == null)
            {
                var failure = error ?? new InvalidOperationException("The refresh produced no result.");
                outcomes = captures.Select(c => RefreshOutcome.Failure(c, failure)).ToList();
            }

            if (suspendCount > 0)
            {
                // A spin started while this was computing: apply when it ends.
                deferred = deferred ?? new List<RefreshOutcome>();
                deferred.AddRange(outcomes);
                return;
            }

            Apply(outcomes);
            Flush();
        }

        private void Apply(List<RefreshOutcome> outcomes)
        {
            var results = new List<WheelRefreshResult>(outcomes.Count);
            var retry = false;
            using (wheels.Batch())
            {
                foreach (var outcome in outcomes)
                {
                    var result = wheels.ApplyRefresh(outcome);
                    results.Add(result);
                    if (result.Kind == RefreshResultKind.Superseded)
                    {
                        // The wheel was edited meanwhile; compute it again from its current state.
                        Enqueue(outcome.WheelId, outcome.Reason, outcome.PinUnmatched);
                        retry = true;
                    }
                    else if (result.Kind == RefreshResultKind.Failed)
                    {
                        errors?.Report(new RandomiserError(result.Category, "refresh wheel", result.ErrorMessage,
                            result.Exception, result.WheelId, "kept previous game list"));
                    }
                }
            }

            Completed?.Invoke(this, new RefreshBatchEventArgs(results));
            if (retry)
            {
                scheduler.Debounce(Flush);
            }
        }

        private void Resume()
        {
            if (--suspendCount > 0 || disposed)
            {
                return;
            }

            if (deferred != null)
            {
                var outcomes = deferred;
                deferred = null;
                Apply(outcomes);
            }

            Flush();
        }

        private void Enqueue(Guid wheelId, RefreshReason reason, bool pinUnmatched)
        {
            if (pending.TryGetValue(wheelId, out var existing))
            {
                existing.Reason = (RefreshReason)Math.Max((int)existing.Reason, (int)reason);
                existing.PinUnmatched |= pinUnmatched;
            }
            else
            {
                pending[wheelId] = new Pending { WheelId = wheelId, Reason = reason, PinUnmatched = pinUnmatched };
            }
        }

        private void OnWheelsChanged(object sender, WheelsChangedEventArgs e)
        {
            if (e.Kind == WheelChangeKind.MembershipRulesChanged && e.WheelId.HasValue)
            {
                InvalidateWheel(e.WheelId.Value, RefreshReason.RulesChanged);
            }
        }

        private sealed class Pending
        {
            public Guid WheelId;
            public RefreshReason Reason;
            public bool PinUnmatched;
        }

        private sealed class Suspension : IDisposable
        {
            private RefreshCoordinator owner;

            public Suspension(RefreshCoordinator owner) => this.owner = owner;

            public void Dispose()
            {
                var coordinator = owner;
                owner = null;
                coordinator?.Resume();
            }
        }
    }
}
