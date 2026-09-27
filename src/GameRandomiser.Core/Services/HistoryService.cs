using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Services
{
    /// <summary>
    /// Records and reads per-wheel spin history. History is purely informational:
    /// it never influences winner selection.
    /// </summary>
    public sealed class HistoryService
    {
        /// <summary>Oldest entries beyond this are discarded to keep the data file small.</summary>
        public const int MaxEntriesPerWheel = 500;

        private readonly WheelService wheels;

        public HistoryService(WheelService wheels)
        {
            this.wheels = wheels ?? throw new ArgumentNullException(nameof(wheels));
        }

        public SpinHistoryEntry Record(Guid wheelId, GameInfo winner)
        {
            if (winner == null)
            {
                throw new ArgumentNullException(nameof(winner));
            }

            var entry = new SpinHistoryEntry
            {
                GameId = winner.Id,
                GameName = winner.Name,
                SpunAtUtc = wheels.Clock.UtcNow
            };

            wheels.UpdateWheel(wheelId, wheel =>
            {
                wheel.History.Insert(0, entry);
                if (wheel.History.Count > MaxEntriesPerWheel)
                {
                    wheel.History.RemoveRange(MaxEntriesPerWheel, wheel.History.Count - MaxEntriesPerWheel);
                }
            }, WheelChangeKind.HistoryChanged);

            return entry;
        }

        /// <summary>Most recent first.</summary>
        public IReadOnlyList<SpinHistoryEntry> GetHistory(Guid wheelId) =>
            wheels.GetWheel(wheelId)?.History.ToList() ?? new List<SpinHistoryEntry>();

        public void Clear(Guid wheelId)
        {
            wheels.UpdateWheel(wheelId, wheel => wheel.History.Clear(), WheelChangeKind.HistoryChanged);
        }

        /// <summary>Optionally drops history entries for games that no longer exist.</summary>
        public int RemoveEntriesFor(IEnumerable<Guid> gameIds)
        {
            var set = new HashSet<Guid>(gameIds);
            var removed = 0;
            foreach (var wheel in wheels.AllWheelsMutable)
            {
                removed += wheel.History.RemoveAll(h => set.Contains(h.GameId));
            }

            if (removed > 0)
            {
                wheels.CommitExternal(WheelChangeKind.HistoryChanged, null);
            }

            return removed;
        }
    }
}
