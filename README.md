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
- **Dynamic wheels** (opt-in): a wheel created from criteria can keep itself in sync with your
  library, strictly or with games you pin by hand.
- **Reroll protection** (opt-in): skip recent winners, limit rerolls, and optional cooldowns.
- A persistent, per-wheel spin history.
- Light, dark or "match Playnite theme" appearance, with an accent colour and segment colours.
- Games can be shown as title, cover, small cover + title, or icon + title.
- A–Z, random or library ordering, plus a Shuffle button that never counts as a spin.
- Works offline. There is no web dependency.

## AI assistance disclosure

This extension was built with AI assistance (Claude). All code has been reviewed, and the project's
direction, design and features have been fully guided by me throughout.

## Install

Download the `.pext` from the [latest release](https://github.com/Kyerstorm/Playnite-Game-Randomiser/releases)
(or build it yourself, see [Building](#building)), then double-click it or drag it onto Playnite,
and restart Playnite.

Upgrading from 1.0 keeps your wheels, history and settings. Nothing changes until you opt in to the
new features: existing wheels stay snapshots and reroll protection is off.

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
  state never affect the odds. The same game can win twice in a row, unless you turn on reroll
  protection (below), which only decides *which* games are eligible; the eligible games are still
  equally likely.
- **Winner stays on the wheel** by default. *Settings → Winner → After winning* offers Keep / Ask
  me / Remove automatically (with Undo).
- **Auto-launch** is off by default. When enabled, it only launches games that are already installed.
- **Hidden games** are never eligible. If a game is hidden, or deleted from the library, it is
  removed from every wheel automatically. **Uninstalled games** stay on wheels, and the winner
  card offers **Install**.
- **Large wheels degrade gracefully.** Labels shrink, wrap, ellipsise and finally hide as segments
  get thin; covers fall back to titles; borders and pegs are dropped for very large wheels.
  Hovering any segment always shows the game's name.

### Dynamic wheels

A wheel created from criteria has a membership policy. Choose it when creating the wheel
(*Keep the wheel up to date*), or later under *Wheels → Updates*.

| Policy | What it does |
|---|---|
| **Snapshot (update manually)**, the default | The list only changes when you edit it. *Refresh from criteria* adds newly matching games and never removes any. This is how every wheel behaved in 1.0. |
| **Keep in sync (strict)** | The wheel is always exactly the games that match. Games that stop matching leave automatically; you can't add games that don't match. |
| **Keep in sync + pinned games** | Matching games, plus games you add or **📌 Pin** yourself. Pinned games stay even if they stop matching. |

- Dynamic wheels are marked **auto**. They refresh when Playnite starts, when the library changes
  in a way their criteria actually read (a playtime change doesn't touch a genre wheel), when you
  change the policy, and when you press *Refresh from criteria*.
- A burst of library changes (for example a library import) is debounced into a single refresh,
  evaluated off the UI thread, and saved once. A refresh never changes a wheel while it's spinning;
  it is applied when the spin finishes.
- Removing a game from a dynamic wheel is remembered, so it doesn't come straight back.
  *Restore removed games* undoes that.
- If a refresh fails, the wheel keeps its last good list, shows **⚠ may be out of date** with the
  reason, and tries again on the next change. A library that suddenly reads as empty is treated as
  a failure, not as "remove everything".
- *Current library filter* wheels can't observe filter changes, so they refresh on startup, on
  library changes and on demand.

### Reroll protection

Off by default. Turn it on in *Settings → Reroll protection*. Settings are global; the state
(recent winners, rerolls used, cooldown) is kept separately for each wheel and saved with it.

| Setting | Effect |
|---|---|
| **Recent winners** | *Don't skip any*, *Skip the previous winner*, or *Skip the last N winners* (1–10). Skipped games can't win the next spin. |
| **Limit rerolls** | After the first spin you get N rerolls (1–10). Then you must **Accept pick** (or Launch / Install the winner) before spinning again. |
| **Cooldown** | Instead of having to accept, rerolls come back by themselves after 1–60 minutes. |
| **Forget protection state** | When Playnite restarts, or only when you press **Reset**. |

Edge cases are handled so the wheel never gets stuck silently: a one-game wheel can always spin;
if every game on a wheel is a recent winner, the spin is blocked with an explanation and a
**Reset** button rather than quietly ignoring your setting; games removed from a wheel or the
library are forgotten; the status line under **SPIN** and the winner card always explain why a
spin is limited and offer the way out.

## Data

| File | Contents |
|---|---|
| `<Playnite>/ExtensionsData/5a1e7c3d-…/data.json` | Wheels, game membership (Playnite game ids only), ordering, criteria, membership policy, pins, reroll state, active wheel, spin history. Versioned via `SchemaVersion` (currently 2), migrated on load, written atomically (with a `.bak`). |
| `<Playnite>/ExtensionsData/5a1e7c3d-…/config.json` | Global settings, saved through Playnite's plugin settings system. |

Game names, covers and install state are never copied into the data file. They are resolved live
from Playnite, so they are always current. History keeps the name at spin time only as a fallback
label, in case the game later leaves your library.

### Data safety

- **Atomic saves.** Data is written to a temporary file, flushed, and swapped into place, keeping
  the previous version as `data.json.bak`. Unchanged data isn't rewritten.
- **Failed saves** leave the file on disk untouched. Your change stays in memory, you get one
  notification, and the next change retries. The notification clears itself once a save succeeds.
- **Unreadable files are never overwritten blindly.** A corrupt file is copied to
  `data.corrupt-<timestamp>.json` and the `.bak` is used if it is readable. A file from a newer
  version (`data.newer-v<N>-…json`), or one whose migration fails (`data.migration-failed-…json`),
  is backed up the same way. If that backup can't be made, the original is not overwritten.
- **Migration.** Version 1 files (1.0) are upgraded on first load, after a
  `data.pre-migration-v1-<timestamp>.json` copy is taken. Every existing wheel becomes a
  *Snapshot* wheel, so nothing behaves differently until you change it.
- Errors are logged to Playnite's `extensions.log` with a category, the operation and the wheel
  involved. Messages shown in Playnite describe what happened and what to do, without stack traces.

## Building

Requires the .NET SDK (6 or later; tested with 10). Visual Studio is not required, because the
`Microsoft.NETFramework.ReferenceAssemblies` package supplies the .NET Framework 4.6.2 targeting pack.

```bash
dotnet build GameRandomiser.sln -c Release
```

```bash
dotnet test GameRandomiser.sln -c Release
```

### Packaging

```powershell
./build/pack.ps1
```

This validates the release metadata, builds the plugin, writes
`artifacts/GameRandomiser_<id>_<version>.pext`, and re-opens the package to verify it: the required
files are present, nothing else is (no test, harness, SDK or Json.NET assemblies), and the manifest
and assembly versions match. `-ValidateOnly` runs just the metadata checks; `-NoBuild` packs the
existing build output. `artifacts/` is not committed.

The version lives in two places that must agree, and the script fails if they don't:
`Version` in `src/GameRandomiser/extension.yaml` (`1.1`) and `<Version>` in `Directory.Build.props`
(`1.1.0`).

### Integration harness

`tools/VisualHarness` hosts the real UI against a mocked Playnite API. It runs actual spins, drives
the dialogs, raises Playnite's library events to exercise dynamic wheels, walks through reroll
protection, forces a failed save and a failed refresh, shuts the plugin down mid-refresh, and
measures UI-thread stalls against a 10,000-game library. It writes a screenshot of every state
(light and dark) and exits non-zero on any failed check or WPF binding error:

```bash
tools/VisualHarness/bin/Release/net462/GameRandomiser.VisualHarness.exe <output-folder>
```

### Continuous integration and releases

`.github/workflows/ci.yml` runs on pull requests, pushes to `main`, tags and manual dispatch, on
`windows-latest`: metadata validation → restore → build → unit tests → integration harness →
package → package validation. The `.pext`, test results and harness screenshots are uploaded as
artifacts.

To release:

1. Set the new version in `extension.yaml` and `Directory.Build.props`, and merge to `main`.
2. Tag it: `git tag v1.1.0 && git push origin v1.1.0`. CI fails if the tag and version differ.
3. CI creates a **draft** GitHub release with the `.pext` attached and generated notes. Review it
   and publish it by hand.
4. Add the new package to `installer.yaml` (version, release date, `PackageUrl` of the published
   asset, changelog) so Playnite's add-on browser offers the update.

## Architecture

```text
src/GameRandomiser.Core         Playnite-free domain logic (unit tested)
  Models/        RandomiserData, RandomiserWheel, SpinHistoryEntry, PopulationSpec, GameInfo,
                 RerollProtectionOptions / RerollState / SelectionResult
  Abstractions/  IGameCatalog (+ LibrarySnapshot), IRandomSource (crypto / seeded),
                 IRandomiserStore, IClock
  Services/      WheelService (wheels, membership policies, pins, ordering, batching, save failures)
                 MembershipReconciler (pure: criteria matches + policy -> new membership)
                 RefreshCoordinator (invalidate -> debounce -> evaluate off-thread -> apply once)
                 RerollProtectionService (pure eligibility stage + per-wheel state)
                 HistoryService (per-wheel history)
                 RandomiserService (equal-probability pick, Fisher–Yates shuffle)
  Diagnostics/   RandomiserError, ErrorCategory, IErrorReporter, UserMessages
  Animation/     WheelGeometry (angles <-> segments), SpinPlanner (winner -> rotation),
                 SpinEasing (accelerate / cruise / decelerate curve with zero end velocity)
  Layout/        LabelLayoutEngine (radial fit / wrap / ellipsis / hide), SegmentPalette (colours, contrast)
  Population/    IPopulationRule + registry (extensible), PopulationEngine
  Persistence/   JsonFileStore (atomic, recovering), DataMigrator (versioned), DataSanitizer

src/GameRandomiser              The Playnite extension (WPF)
  GameRandomiserPlugin          Lifecycle, sidebar item, menus, settings
  Services/RandomiserContext    Composition root; library change events -> cleanup + refresh;
                                refresh / save failure notifications
  Services/                     DispatcherRefreshScheduler, AudioService, ImageCache, ThemeService
  Integration/                  PlayniteGameCatalog, PlayniteGameActions, ContextMenuBuilder
  UI/                           SidebarView (+ViewModel), WheelControl, ConfettiLayer,
                                WheelCreationWindow, GamePickerWindow, Styles.xaml
  Settings/                     RandomiserSettings, settings view model and view, ColourPickerButton

tests/GameRandomiser.Core.Tests xUnit on net462 (same runtime as Playnite)
tools/VisualHarness             Screenshot + end-to-end harness
```

A spin runs in five separate stages:

1. `RerollProtectionService` decides which segments are eligible (all of them when protection is off).
2. `RandomiserService` picks the winner uniformly among the eligible segments.
3. `SpinPlanner` computes the rotation.
4. `SpinPlan.RotationAt` + `SpinEasing` interpolate each frame.
5. `SidebarViewModel` and the view present the winner, and record history and protection state
   in one save.

A dynamic wheel refresh is a pipeline too. Library events are diffed into the aspects that changed
(`LibraryFields`); `RefreshCoordinator` marks only the wheels whose rules read those aspects,
debounces, snapshots the library, evaluates the rules on a worker thread, and applies all results
on the UI thread in one batch. Each wheel carries a revision number, so a result computed against
an older version of a wheel is discarded rather than overwriting a newer edit. While a wheel is
spinning the coordinator is suspended, and nothing is delivered after the plugin is disposed.

The wheel is drawn into cached `DrawingVisual` layers. Each animation frame only changes a
`RotateTransform` angle, so frame cost doesn't grow with the number of games or covers. Artwork
is decoded off the UI thread at the size it's drawn, and never during a spin.

## Notes and limitations

- Playnite doesn't expose its audio engine to extensions. Sounds are synthesised in memory and
  played with the Windows `PlaySound` API, with volume and an on/off switch in Settings.
- Extensions can't open a sidebar item programmatically, so menu entries open the same
  Randomiser view in a window.
- *Current library filter* uses whatever filter is active in Playnite's library view at the moment
  the wheel is created or refreshed.
- The criteria of an existing wheel can't be edited yet. Create a new wheel (or duplicate one) to
  change criteria; the membership policy can be changed at any time.
- Reroll protection settings are global. The recent-winner and reroll state is per wheel.
