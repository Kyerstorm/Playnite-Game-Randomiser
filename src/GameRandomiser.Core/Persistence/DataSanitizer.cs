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
        private const int MaxErrorLength = 500;

        private static List<Guid> CleanIds(List<Guid> ids) =>
            (ids ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();

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

                if (!Enum.IsDefined(typeof(MembershipPolicy), wheel.MembershipPolicy))
                {
                    wheel.MembershipPolicy = MembershipPolicy.ManualSnapshot;
                }

                if (wheel.Population != null)
                {
                    wheel.Population.ItemIds = (wheel.Population.ItemIds ?? new List<Guid>())
                        .Where(id => id != Guid.Empty)
                        .Distinct()
                        .ToList();
                    var amount = wheel.Population.Amount;
                    if (amount.HasValue && (double.IsNaN(amount.Value) || double.IsInfinity(amount.Value) || amount.Value < 0))
                    {
                        // The rule's default amount is used instead.
                        wheel.Population.Amount = null;
                    }

                    if (!Enum.IsDefined(typeof(PopulationSource), wheel.Population.Source))
                    {
                        // Unknown rule (hand edit or newer version): keep the games and the criteria text,
                        // but never evaluate it automatically.
                        wheel.MembershipPolicy = MembershipPolicy.ManualSnapshot;
                    }
                }
                else
                {
                    wheel.MembershipPolicy = MembershipPolicy.ManualSnapshot;
                }

                wheel.PinnedGameIds = CleanIds(wheel.PinnedGameIds);
                wheel.ExcludedGameIds = CleanIds(wheel.ExcludedGameIds);
                if (wheel.RefreshError != null)
                {
                    wheel.RefreshError = string.IsNullOrWhiteSpace(wheel.RefreshError)
                        ? null
                        : wheel.RefreshError.Length > MaxErrorLength ? wheel.RefreshError.Substring(0, MaxErrorLength) : wheel.RefreshError;
                }

                if (wheel.Reroll != null)
                {
                    wheel.Reroll.Sanitize();
                    if (wheel.Reroll.IsEmpty)
                    {
                        wheel.Reroll = null;
                    }
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
