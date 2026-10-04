using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using GameRandomiser.Core.Services;
using Playnite.SDK;

namespace GameRandomiser.Services
{
    /// <summary>
    /// Runs the refresh coordinator's work the WPF way: a restartable dispatcher timer for debouncing,
    /// the thread pool for evaluating criteria, and the dispatcher to hand results back to the UI thread.
    /// After <see cref="Dispose"/> nothing is delivered, so a refresh in flight during shutdown is simply dropped.
    /// </summary>
    public sealed class DispatcherRefreshScheduler : IRefreshScheduler
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly Dispatcher dispatcher;
        private readonly TimeSpan interval;
        private DispatcherTimer timer;
        private Action pending;
        private volatile bool disposed;

        public DispatcherRefreshScheduler(Dispatcher dispatcher, TimeSpan interval)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.interval = interval;
        }

        public void Debounce(Action callback)
        {
            if (disposed)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => Debounce(callback)));
                return;
            }

            // Restarting the timer on every call is what turns a burst of library events into one refresh;
            // keeping only the latest callback means the final request always runs.
            pending = callback;
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
                timer.Tick += OnTick;
            }

            timer.Stop();
            timer.Start();
        }

        public void Run<T>(Func<T> work, Action<T, Exception> completed)
        {
            Task.Run(work).ContinueWith(task =>
            {
                // Reading Exception observes it, so a failed refresh can never become an unobserved task exception.
                var error = task.Exception?.GetBaseException();
                var result = task.Status == TaskStatus.RanToCompletion ? task.Result : default(T);
                if (disposed)
                {
                    return;
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed)
                    {
                        return;
                    }

                    try
                    {
                        completed(result, error);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, "Game Randomiser failed to apply a wheel refresh.");
                    }
                }));
            }, TaskScheduler.Default);
        }

        public void Dispose()
        {
            disposed = true;
            pending = null;
            var current = timer;
            if (current == null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                current.Stop();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(current.Stop));
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            timer.Stop();
            var callback = pending;
            pending = null;
            if (disposed || callback == null)
            {
                return;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Game Randomiser failed to start a wheel refresh.");
            }
        }
    }
}
