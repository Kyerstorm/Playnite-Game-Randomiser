using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Models;

namespace GameRandomiser.Core.Persistence
{
    /// <summary>
    /// Repairs structurally invalid documents (hand edits, partial writes, older bugs)
    /// so the rest of the extension can assume well-formed data.
    /// </summary>
    public static class DataSanitizer
    {
        public static RandomiserData Sanitize(RandomiserData data)
        {
            data = data ?? new RandomiserData();
            data.Wheels = data.Wheels ?? new List<RandomiserWheel>();

            var seenWheelIds = new HashSet<Guid>();
            var cleanWheels = new List<RandomiserWheel>();
            foreach (var wheel in data.Wheels.Where(w => w != null))
            {
                if (wheel.Id == Guid.Empty || !seenWheelIds.Add(wheel.Id))
                {
                    wheel.Id = Guid.NewGuid();
                    seenWheelIds.Add(wheel.Id);
                }

                wheel.Name = string.IsNullOrWhiteSpace(wheel.Name) ? "Untitled wheel" : wheel.Name.Trim();
                wheel.Icon = string.IsNullOrWhiteSpace(wheel.Icon) ? "🎲" : wheel.Icon;
                wheel.GameIds = (wheel.GameIds ?? new List<Guid>())
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();
                wheel.History = (wheel.History ?? new List<SpinHistoryEntry>())
                    .Where(h => h != null && h.GameId != Guid.Empty)
                    .OrderByDescending(h => h.SpunAtUtc)
                    .ToList();
                if (!Enum.IsDefined(typeof(SortMode), wheel.SortMode))
                {
                    wheel.SortMode = SortMode.Alphabetical;
                }

                if (wheel.Population != null)
                {
                    wheel.Population.ItemIds = wheel.Population.ItemIds ?? new List<Guid>();
                }

                cleanWheels.Add(wheel);
            }

            data.Wheels = cleanWheels;

            if (data.ActiveWheelId.HasValue && data.Wheels.All(w => w.Id != data.ActiveWheelId.Value))
            {
                data.ActiveWheelId = null;
            }

            if (!data.ActiveWheelId.HasValue && data.Wheels.Count > 0)
            {
                data.ActiveWheelId = data.Wheels[0].Id;
            }

            return data;
        }
    }
}
