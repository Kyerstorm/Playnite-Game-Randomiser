using System;
using System.Collections.Generic;
using System.Linq;
using GameRandomiser.Core.Models;
using GameRandomiser.Services;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameRandomiser.Integration
{
    /// <summary>
    /// Builds the "Game Randomiser" section of Playnite's game context menu. Actions adapt to the
    /// selection: add or remove for the current wheel, quick targets for a handful of other wheels,
    /// and a searchable chooser once there are too many wheels for a tidy menu.
    /// </summary>
    public sealed class ContextMenuBuilder
    {
        public const string Section = "Game Randomiser";
        private const int QuickWheelLimit = 5;

        private readonly RandomiserContext context;

        public ContextMenuBuilder(RandomiserContext context)
        {
            this.context = context;
        }

        public IEnumerable<GameMenuItem> Build(IList<Game> selection)
        {
            var eligible = (selection ?? new List<Game>()).Where(g => g != null && !g.Hidden).Select(g => g.Id).Distinct().ToList();
            if (eligible.Count == 0)
            {
                // Hidden games can never be on a wheel, so there's nothing sensible to offer.
                return Enumerable.Empty<GameMenuItem>();
            }

            var items = new List<GameMenuItem>();
            var wheels = context.Wheels.Wheels;
            if (wheels.Count == 0)
            {
                items.Add(Item(eligible.Count == 1 ? "Create a wheel with this game…" : $"Create a wheel with these {eligible.Count} games…",
                    () => context.CreateWheelInteractive(eligible)));
                items.Add(Item("-", null));
                items.Add(Item("Open Randomiser", () => context.OpenWindow()));
                return items;
            }

            var active = context.Wheels.ActiveWheel;
            var single = wheels.Count == 1;
            var target = single ? "Randomiser" : active.Name;
            var missing = eligible.Where(id => !active.GameIds.Contains(id)).ToList();
            var present = eligible.Where(id => active.GameIds.Contains(id)).ToList();
            var activeId = active.Id;

            if (missing.Count > 0)
            {
                var label = eligible.Count == 1 ? $"Add to {target}" : $"Add {missing.Count} games to {target}";
                items.Add(Item(label, () => AddTo(activeId, missing)));
            }

            if (present.Count > 0)
            {
                var label = eligible.Count == 1 ? $"Remove from {target}" : $"Remove {present.Count} games from {target}";
                items.Add(Item(label, () => context.SafeRun("remove games", () => context.Wheels.RemoveGames(activeId, present))));
            }

            if (!single)
            {
                var others = wheels.Where(w => w.Id != activeId).ToList();
                var addable = others.Where(w => eligible.Any(id => !w.GameIds.Contains(id))).ToList();
                var addSection = Section + "|Add to…";
                foreach (var wheel in addable.Take(QuickWheelLimit))
                {
                    var wheelId = wheel.Id;
                    items.Add(Item($"{wheel.Icon}  {wheel.Name}", () => AddTo(wheelId, eligible), addSection));
                }

                if (addable.Count > QuickWheelLimit)
                {
                    items.Add(Item("Choose wheel…", () => ChooseAndAdd(eligible), addSection));
                }

                var removable = others.Where(w => eligible.Any(w.GameIds.Contains)).ToList();
                var removeSection = Section + "|Remove from…";
                foreach (var wheel in removable.Take(QuickWheelLimit + 3))
                {
                    var wheelId = wheel.Id;
                    items.Add(Item($"{wheel.Icon}  {wheel.Name}",
                        () => context.SafeRun("remove games", () => context.Wheels.RemoveGames(wheelId, eligible)), removeSection));
                }
            }

            items.Add(Item(eligible.Count == 1 ? "Add to a new wheel…" : $"Add {eligible.Count} games to a new wheel…",
                () => context.CreateWheelInteractive(eligible)));
            items.Add(Item("-", null));
            items.Add(Item("Open Randomiser", () => context.OpenWindow()));
            return items;
        }

        private void AddTo(Guid wheelId, IList<Guid> games)
        {
            var wheel = context.Wheels.GetWheel(wheelId);
            if (wheel != null)
            {
                context.AddGamesWithFeedback(wheel, games);
            }
        }

        private void ChooseAndAdd(IList<Guid> games)
        {
            var wheel = context.ChooseWheel(games.Count == 1 ? "Add game to…" : $"Add {games.Count} games to…", games);
            if (wheel != null)
            {
                context.AddGamesWithFeedback(wheel, games);
            }
        }

        private static GameMenuItem Item(string description, Action action, string section = Section) => new GameMenuItem
        {
            Description = description,
            MenuSection = section,
            Action = action == null ? (Action<GameMenuItemActionArgs>)null : _ => action()
        };
    }
}
