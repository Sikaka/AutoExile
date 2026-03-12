# AutoExile

ExileCore plugin for automated Path of Exile gameplay. Built on game state data from POEMCP research.

## Features

### Farming Modes

**Mapping** — Automated map exploration with combat, looting, and mechanic handling.
- Blob-based exploration with coverage tracking (70% threshold)
- A* pathfinding with blink-aware gap crossing and stuck recovery
- In-map mechanic detection (Ultimatum supported, extensible)
- F5 hotkey for activate/pause/resume

**Blight** — Full blight encounter automation.
- 14-phase state machine: hideout stash → map device → pump → towers → sweep → loot → exit
- Lane reconstruction with threat/coverage/danger scoring
- Tower build/upgrade state machine with tier-3 branching and spread rules
- Hunt + patrol sweep modes for post-timer cleanup

**Simulacrum** — Simulacrum wave farming through all 15 waves.
- Monolith detection, wave state tracking, spawn zone heatmapping
- Between-wave stashing via in-map stash
- Death handling with automatic re-entry
- Loot sweep and stash before exit

**Follower** — Follow a named party leader through zones. Built, not yet tested in live multiplayer.

### Combat

- Per-skill slot configuration: role, priority, conditions (buff/debuff checks, enemy count, rarity filter, range, low life)
- Positioning modes: Aggressive, Orbit, Ranged, Stationary
- Flask management with life/mana thresholds and utility intervals
- Vaal skill and summon recast support
- Buff scanner UI for discovering buff/debuff names in-game

### Loot

- Respects in-game loot filter (visible labels only)
- Unique pricing via NinjaPrice plugin bridge
- Per-slot value scoring with configurable thresholds
- Failed pickup blacklist to prevent retry loops
- Session tracking: chaos earned, chaos/hour, recent pickups overlay

### Navigation

- Weighted A* on terrain pathfinding grid with blink edge support
- Gap vs wall detection using targeting layer discrimination
- Path smoothing with line-of-sight checks
- Stuck recovery: door/breakable interaction, micro-movement nudges
- Blob-based exploration with region segmentation and failed-region tracking

### In-Map Mechanics

- **Ultimatum** — Full encounter state machine: altar detection, mod selection (danger-rated), combat with leash radius, reward pricing. Configurable danger threshold and encounter type filtering.
- Extensible `IMapMechanic` interface for future mechanics (Breach, Harvest, Heist, etc.)

### Utilities

- Map device automation (navigate, open atlas, select map, activate, enter portal)
- Stash interaction (navigate, open, Ctrl+click items, incubator application)
- Tile map reading for landmark navigation (doors, exits, mechanics)
- Game state dump (F6): terrain + exploration + pathfinding snapshot to PNG + JSON
- Auto gem leveling
- Global input gate with async cursor settle and key hold timing

## Project Status

| Component | Status |
|---|---|
| Mapping Mode | Complete |
| Blight Mode | Complete |
| Simulacrum Mode | Complete |
| Follower Mode | Complete, untested |
| Ultimatum Mechanic | Complete |
| All 16 Systems | Complete |
| Core (BotCore, Settings, Context) | Complete |
| Debug Tooling | Complete |
| Automated Tests | Not started |
| Campaign Mode | Planned |
| Heist Mode | Planned |

## Requirements

- [ExileApi](https://github.com/exApiTools/ExileApi-Compiled/) with plugin support
- .NET 10.0 (Windows)
- [Get-Chaos-Value](https://github.com/DetectiveSquirrel/Get-Chaos-Value) plugin for unique item valuation (optional)

## Build

```
dotnet build
```

Deploy DLL to ExileCore plugins directory. Reload plugins in ExileCore if the DLL is locked.

## Configuration

All settings are exposed through the ExileCore plugin settings panel. Key sections:

- **Build Settings** — Movement keys, skill slots (1-6), combat positioning, flask setup
- **Loot Settings** — Pickup radius, unique value thresholds, per-slot minimum
- **Blight Settings** — Tower priorities, build radius, sweep timing, patrol leash
- **Simulacrum Settings** — Max deaths, wave timeout, stash threshold
- **Follower Settings** — Leader name, follow/stop distances

Each skill slot supports granular conditions: buff/debuff gating, enemy count, rarity filter, range limits, low life triggers, and summon recast. Use the built-in Scan button to discover buff names from live gameplay.
