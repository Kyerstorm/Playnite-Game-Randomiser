using System;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Abstractions
{
    /// <summary>Persists the randomiser document.</summary>
    public interface IRandomiserStore
    {
        StoreLoadResult Load();

        void Save(RandomiserData data);
    }

    public sealed class StoreLoadResult
    {
        public StoreLoadResult(RandomiserData data, string warning = null)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Warning = warning;
        }

        public RandomiserData Data { get; }

        /// <summary>Non-null when data had to be recovered (e.g. corrupt file backed up and reset).</summary>
        public string Warning { get; }
    }

    /// <summary>Clock abstraction so history timestamps and "recently played" are testable.</summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
