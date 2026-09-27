# 🎲 Game Randomiser for Playnite

A Wheel-of-Names style game picker built natively into Playnite's sidebar. You choose the pool;
the wheel makes a transparent, equal-probability decision.

- Multiple named wheels, switchable from the sidebar
- Add games by right-clicking them in your library (one or many at once)
- Create wheels automatically: all games, the current library filter, genre, platform, tag,
  category, play status, never played / played, playtime above or below X hours, recently played,
  installed / not installed. A live "games found" count is shown before you create the wheel.
- An animated spin that accelerates, cruises, and then decelerates naturally, with synchronised
  tick sounds and a pointer that flicks as pegs pass.
- A winner card with artwork and native actions: **Spin again**, **Remove**, **Install**,
  **Launch** and **View details**.
- A persistent, per-wheel spin history.
- Light, dark or "match Playnite theme" appearance, with an accent colour and segment colours.
- Games can be shown as title, cover, small cover + title, or icon + title.
- A–Z, random or library ordering, plus a Shuffle button that never counts as a spin.
- Works offline. There is no web dependency.

## AI assistance disclosure

This extension was built with AI assistance (Claude). All code has been reviewed, and the project's
direction, design and features have been fully guided by me throughout.

## Install

Double-click `dist/GameRandomiser_5a1e7c3d-9b2f-4e68-a0c4-7d3b91f2e865_1_0.pext`, or drag it onto
Playnite, then restart Playnite.

Manual install: copy the contents of `src/GameRandomiser/bin/Release/` into
`<Playnite>/Extensions/GameRandomiser_5a1e7c3d-9b2f-4e68-a0c4-7d3b91f2e865/` and restart Playnite.

Requires Playnite 10 (SDK 6.x).

## Using it

| Where | What |
|---|---|
| **Sidebar → Game Randomiser** | The wheel. Pick a wheel, press **SPIN** or click the wheel. **Wheels** tab: create, rename, change icon, duplicate, clear, delete, and add or remove games. **History** tab: recent picks (double-click a pick to open it in your library). The ⚙ button opens Settings. |
| **Right-click game(s) → Game Randomiser** | Add to / remove from the current wheel. Quick targets for up to five other wheels, then *Choose wheel…* once there are more. *Add to a new wheel…*. *Open Randomiser*. |
| **Main menu → Extensions → Game Randomiser** | Open Randomiser (as a window), Create wheel…, Manage wheels, Settings. |

The first time you open the Randomiser, it offers to create your first wheel.

### Behaviour worth knowing

- **Fairness.** Every game on a wheel has exactly `1/N` chance. The winner is drawn (with a
  cryptographic RNG and rejection sampling, so there's no modulo bias) *before* the animation
  starts, and the animation is then solved to land on it. Ordering, history, playtime and install
  state never affect the odds. The same game can win twice in a row.
- **Winner stays on the wheel** by default. *Settings → Winner → After winning* offers Keep / Ask
  me / Remove automatically (with Undo).
- **Auto-launch** is off by default. When enabled, it only launches games that are already installed.
- **Hidden games** are never eligible. If a game is hidden, or deleted from the library, it is
  removed from every wheel automatically. **Uninstalled games** stay on wheels, and the winner
  card offers **Install**.
- **Automatic wheels are snapshots.** The criteria are stored with the wheel, and *Wheels → Refresh
  from criteria* adds any newly matching games. The data model reserves `IsDynamic` so wheels can
  be kept continuously in sync in a future version.
- **Large wheels degrade gracefully.** Labels shrink, wrap, ellipsise and finally hide as segments
  get thin; covers fall back to titles; borders and pegs are dropped for very large wheels.
  Hovering any segment always shows the game's name.

## Data

| File | Contents |
|---|---|
| `<Playnite>/ExtensionsData/5a1e7c3d-…/data.json` | Wheels, game membership (Playnite game ids only), ordering, criteria, active wheel, spin history. Versioned via `SchemaVersion`, migrated on load, written atomically (with a `.bak`). A corrupt or newer-version file is backed up before anything is overwritten. |
| `<Playnite>/ExtensionsData/5a1e7c3d-…/config.json` | Global settings, saved through Playnite's plugin settings system. |

Game names, covers and install state are never copied into the data file. They are resolved live
from Playnite, so they are always current. History keeps the name at spin time only as a fallback
label, in case the game later leaves your library.

## Building

Requires the .NET SDK (6 or later; tested with 10). Visual Studio is not required, because the
`Microsoft.NETFramework.ReferenceAssemblies` package supplies the .NET Framework 4.6.2 targeting pack.

```bash
dotnet build GameRandomiser.sln -c Release
dotnet test tests/GameRandomiser.Core.Tests -c Release
"<Playnite>/Toolbox.exe" pack src/GameRandomiser/bin/Release dist
```

### Visual harness

`tools/VisualHarness` hosts the real UI against a mocked Playnite API. It runs actual spins, drives
the dialogs, captures WPF binding errors, checks that every winner lands under the pointer, and
writes screenshots:

```bash
tools/VisualHarness/bin/Release/GameRandomiser.VisualHarness.exe <output-folder>
```

## Architecture

```text
src/GameRandomiser.Core         Playnite-free domain logic (unit tested)
  Models/        RandomiserData, RandomiserWheel, SpinHistoryEntry, PopulationSpec, GameInfo
  Abstractions/  IGameCatalog, IRandomSource (crypto / seeded), IRandomiserStore, IClock
  Services/      WheelService (wheels, membership, ordering, shuffle, cleanup)
                 HistoryService (per-wheel history)
                 RandomiserService (equal-probability pick, Fisher–Yates shuffle)
  Animation/     WheelGeometry (angles <-> segments), SpinPlanner (winner -> rotation),
                 SpinEasing (accelerate / cruise / decelerate curve with zero end velocity)
  Layout/        LabelLayoutEngine (radial fit / wrap / ellipsis / hide), SegmentPalette (colours, contrast)
  Population/    IPopulationRule + registry (extensible), PopulationEngine
  Persistence/   JsonFileStore (atomic, recovering), DataMigrator (versioned), DataSanitizer

src/GameRandomiser              The Playnite extension (WPF)
  GameRandomiserPlugin          Lifecycle, sidebar item, menus, settings
  Services/RandomiserContext    Composition root; library change events -> cleanup
  Services/                     AudioService, ImageCache, ThemeService
  Integration/                  PlayniteGameCatalog, PlayniteGameActions, ContextMenuBuilder
  UI/                           SidebarView (+ViewModel), WheelControl, ConfettiLayer,
                                WheelCreationWindow, GamePickerWindow, Styles.xaml
  Settings/                     RandomiserSettings, settings view model and view, ColourPickerButton

tests/GameRandomiser.Core.Tests xUnit on net462 (same runtime as Playnite)
tools/VisualHarness             Screenshot + end-to-end harness
```

A spin runs in four separate stages:

1. `RandomiserService` picks the winner.
2. `SpinPlanner` computes the rotation.
3. `SpinPlan.RotationAt` + `SpinEasing` interpolate each frame.
4. `SidebarViewModel` and the view present the winner.

The wheel is drawn into cached `DrawingVisual` layers. Each animation frame only changes a
`RotateTransform` angle, so frame cost doesn't grow with the number of games or covers. Artwork
is decoded off the UI thread at the size it's drawn, and never during a spin.

## Notes and limitations

- Playnite doesn't expose its audio engine to extensions. Sounds are synthesised in memory and
  played with the Windows `PlaySound` API, with volume and an on/off switch in Settings.
- Extensions can't open a sidebar item programmatically, so menu entries open the same
  Randomiser view in a window.
- *Current library filter* uses whatever filter is active in Playnite's library view at the moment
  you create the wheel.
