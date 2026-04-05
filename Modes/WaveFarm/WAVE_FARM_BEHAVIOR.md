# Wave Farm Mode — Behavior Specification

How the bot decides what to do each tick during wave farming. Review for logical mistakes before testing.

## Overall Loop

```
Hideout → Stash/Fragments → Open Map → Enter Portal → [Wave Tick Loop] → Exit → Hideout
```

- **HideoutFlow** handles: settle → stash → withdraw fragments → map device → enter portal
- If the bot dies or returns to hideout unexpectedly, it re-enters via the existing portal
- Run counter increments only when the bot exits a map it marked as "complete"

## Sub-Zone (Wish/Mirage) Lifecycle

Wish zones share the parent map's area name but have a different **zone hash**. The bot detects transitions via hash change.

**Entering sub-zone:**
1. Hash changes while in `InMap` phase → detected as sub-zone entry
2. **Parent zone state saved** to ZoneStateCache (exploration + ThreatMap + loot metrics)
3. Parent hash saved, `_isInSubZone = true`
4. SekhemaPortal position cached (exit portal entity)
5. Entity-ID-based state reset (loot failed, combat unreachable — entities differ per zone)
6. Fresh ThreatMap initialized for sub-zone terrain
7. 1-second settle delay, then exploration reinit with new terrain data

**Clearing sub-zone:**
- Treated identically to the main map — same tick priority, same combat/loot/explore logic
- Failed explore targets cleared on zone transition (fresh slate)

**Exiting sub-zone (when WaveTick returns "done"):**
1. Try UI exit button (bottom-right, `IngameUi[142][7][17][0]`) — fastest, most reliable
2. Fallback: walk to SekhemaPortal entity if visible and targetable
3. Fallback: walk to cached portal position
4. Last resort: explore to reveal the portal entity
5. **No portal scrolls** — they don't work in wish zones

**Returning to parent:**
- Hash changes back to parent hash → `_isInSubZone = false`
- **Parent zone state restored** from ZoneStateCache (exploration coverage + ThreatMap data preserved)
- Entity-ID-based state reset (fresh entity cache rebuild from current entities)
- No re-exploration of already-cleared areas

## Tick Priority (WaveTick.Tick)

Each tick, the bot runs through this list top-to-bottom. The first thing that "wins" controls what happens.

### Pre-priority: Always-on systems

These run every tick regardless of decision:

1. **Active mechanic tick** — if a mechanic (Ritual, Ultimatum, etc.) is active, it controls the bot entirely. The mechanic calls combat.Tick() itself with its own positioning rules. Nothing below runs.
2. **Combat.Tick()** — skills fire continuously. Flask management. Threat scanning. This happens every tick even during exploration. Combat does NOT block other decisions (it's not a state). Whether combat repositioning is allowed depends on `hasDensityEngagement`.
3. **Pack engagement check** — see "Combat Engagement" below. If engaged, this takes over.
4. **Dormant monster approach** — if not in combat and there's a dormant (alive but not targetable) monster within 60 grid, walk toward it. Map bosses need proximity to activate.

### Priority List (EvaluateBestAction)

If none of the above took over, the bot evaluates this priority list:

| Priority | Name | Condition | Action |
|---|---|---|---|
| P1 | Forward loot | Any candidate within 25 grid OR ahead of walk direction | Pick up nearest qualifying item |
| P2 | Deferred mechanic | A deferred mechanic is now ready to engage | Navigate to it |
| P3 | Backtrack loot | Any candidate behind the player worth >= 50c | Pick up highest-value item |
| P3b | Missed loot | Previously failed pickup worth >= 5c, entity still exists | Navigate back to it |
| P4 | Explore (ThreatMap) | Coverage < MinCoverage AND ThreatMap has alive monsters | Navigate toward densest alive chunk (map-wide persistent tracking) |
| P4 | Explore (terrain) | Coverage < MinCoverage, no ThreatMap targets | Navigate to next unexplored area |
| P4b | Hunt monsters | Coverage met, ThreatMap has alive monsters | Navigate toward nearest alive chunk |
| P5 | Post-clear | Plan has post-clear actions (e.g., ritual shop) | Execute plan action |
| P6 | Label toggle | No loot nearby, labels may be stale | Toggle Z key to refresh ground labels |
| P7 | Exit map | Nothing left to do | Signal map complete → exit flow |

### How "Forward" and "Backtrack" work

The bot tracks its recent movement direction. Items are classified as:
- **Forward**: within the forward hemisphere (180 degrees) of the walk direction
- **Within grab radius**: distance <= 25 grid — ALWAYS grabbed regardless of direction
- **Backtrack**: behind the player AND worth >= BacktrackLootThreshold (50c by default)

Items between 25 grid and ahead = grabbed. Items > 25 grid and behind but < 50c = skipped entirely.

### Loot Candidates

`LootSystem.Scan()` runs every 500ms. It reads `VisibleGroundItemLabels` and:
1. Skips items in the failed list (previously failed pickup with cooldown)
2. Skips gold (auto-pickup)
3. Uses a per-entity cache to avoid re-evaluating items
4. Applies value filters (Ninja prices, rarity thresholds, must-loot lists)
5. Sorts by distance (nearest first)

Items NOT in candidates will never be picked up. Common reasons:
- Below value threshold (no Ninja price or too cheap)
- In the failed list (previous pickup attempt failed)
- Label not visible (game loot filter hiding it, or label toggle needed)

### Navigation Stuck Recovery (NEW)

When EvaluateBestAction returns an Explore target and `NavigateTo()` fails (no path found):
1. The target position is added to a **failed explore targets** list
2. An immediate fallback to standard terrain-based exploration runs
3. Future ticks skip monster targets within 30 grid of any failed position
4. Failed targets expire after 30 seconds (entities may have moved/despawned)
5. Failed targets are cleared on zone transitions (sub-zone entry/exit)

This prevents the bot from sitting idle when stale entity data points to unreachable positions.

## Combat Engagement

The bot uses a **density-gated engagement lock** for close-range builds (RF, melee).

### Trigger conditions (any one):

1. **Dense pack**: `WeightedDensity >= MinPackDensity` (default: 5) while InCombat
   - Rarity weights: Normal=1, Magic=2, Rare=5, Unique=8
   - A lone rare (weight 5) triggers at threshold 5; two normals (weight 2) don't
2. **Rare/unique detour**: `DetourForRares` enabled AND BestTarget is rare/unique AND within MaxDetourDistance (60 grid)

### During engagement:

- **Engagement lock**: once triggered, stays locked until ALL nearby monsters are dead OR 10-second timeout
- **Movement**: continuously walks toward `DenseClusterCenter` (weighted center of nearby pack)
- **Loot during fight**: scans every 500ms, picks up items opportunistically between combat moves
- **Skills**: fire continuously (combat is always-on)

### InCombat definition:

InCombat = at least 1 alive hostile monster within CombatRange (user setting, typically 20-30 for RF builds). This is the **combat range**, not awareness range. Monsters beyond CombatRange are tracked by the entity list but don't trigger InCombat.

### Target focus timeout:

If the bot attacks the same target for too long without killing it, the target gets deprioritized:
- Normal: 2s, Magic: 3s, Rare: 5s, Unique: 10s
- Deprioritized targets get -200 score penalty (other targets win, but the deprioritized target isn't blacklisted)

## ThreatMap — Persistent Monster Tracking

A map-wide spatial grid that records every monster observed during the run.

### How it works:
- Map divided into 40x40 grid-unit **chunks** (~13x13 chunks for City Square)
- **Callback-driven** — no entity list iteration. Hooks into EntityAdded/EntityRemoved.
- Per chunk tracks: alive count, dead count, rarity-weighted density, entity IDs
- **Reconciliation** every 250ms: checks nearby chunks (~200 grid radius) for monster deaths by querying EntityCache
- Persisted across sub-zone transitions via ZoneStateCache

### Monster lifecycle in ThreatMap:
1. **EntityAdded** (monster enters network bubble): recorded in chunk as Alive
2. **Monster dies within bubble**: reconciliation detects IsAlive=false, marks Dead in chunk
3. **EntityRemoved** (monster leaves bubble alive): marked LeftRange — chunk keeps its alive count (monster is probably still there)
4. **EntityAdded again** (player returns): if was LeftRange and now dead, marks Dead

### What it provides:
- `GetDensestAliveChunk(playerPos)` — chunk with most alive weight, used for P4 exploration
- `GetNearestAliveChunk(playerPos)` — nearest chunk with alive monsters
- `GetThreatInRadius(center, radius)` — total alive weight in an area
- `TotalAlive / TotalTracked / TotalDead` — map-wide counters for overlay

## Exploration

### Monster-biased exploration (P4):

When coverage < MinCoverage, the bot prefers navigating toward known monster clusters:
1. **ThreatMap densest chunk** — highest rarity-weighted alive count across the entire map
2. **ThreatMap nearest alive chunk** — fallback if densest is blocked/failed
3. **Standard terrain exploration** — walkable unexplored areas (always reachable)

All targets are filtered against the failed explore targets list.

### Standard exploration:

`ExplorationMap.GetNextExplorationTarget()` returns the nearest unexplored walkable cell. The exploration system uses blob-based coverage tracking. Coverage is the % of walkable cells visited in the current connected region.

### MinCoverage setting:

- Default: 0.85 (85%)
- Once coverage reaches this threshold, the bot considers the map "explored enough"
- It then switches to P4b (hunt remaining monsters via ThreatMap) before eventually exiting

## Mechanic Handling

- **Detection**: scanned every 1000ms via `MapMechanicManager.DetectAndPrioritize()`
- **Deferral**: the plan decides if a mechanic should be deferred (engage later when backtracking). AlchAndGoPlan never defers.
- **Engagement**: mechanics take full control when active — they manage their own combat, positioning, and loot

### Ritual specifics:
- Leash radius: 40 grid around the altar
- Combat positioning enabled during ritual fights
- Loot scanned every 500ms during fighting, filtered to within leash radius
- After fighting, looting sweep with 15s hard cap

## Exit Map Flow

1. WaveTick returns `true` (ExitMap action)
2. If in sub-zone: exit via SekhemaPortal (see Sub-Zone section above)
3. If in main map: press PortalKey (from settings), wait 1.5s for portal, click portal entity
4. Portal failure: after 3s, check if we're actually in a wish zone (exit button check), otherwise retry

## Debug Overlay

The overlay shows these lines (top to bottom):
- **Phase + status**: `[InMap] Exploring (79%)`
- **Decision**: which priority won this tick (`Explore`, `PickupLoot`, `PackEngage`, etc.)
- **Coverage**: `79%  Regions: 113`
- **Threats**: `245 alive / 350 seen / 105 dead` — ThreatMap counters (map-wide persistent tracking)
- **Combat**: monster count, weighted density, best target name
- **Nav**: waypoint progress, destination, stuck recovery count
- **Move**: movement layer active/off/suspended
- **Loot items**: count of candidates nearby
- **Interact**: current interaction status
- **Loot stats**: `11/39 (28%)` = 11 successful pickups out of 39 attempts
- **LootDbg** (NEW): why loot was/wasn't chosen — shows nearest candidate details or skip reason
- **Mechanic**: active mechanic name and status
- **Skill**: last combat skill action

## Settings That Matter

| Setting | Default | Effect |
|---|---|---|
| `MinPackDensity` | 5 (weighted) | Density threshold to interrupt exploration for combat |
| `DetourForRares` | true | Always chase rares/uniques within range |
| `MaxDetourDistance` | 60 grid | Max chase distance for rare detour |
| `MinCoverage` | 0.85 | Exploration % before considering map done |
| `PortalKey` | F | Key to open portal scroll |
| `CombatRange` | varies (20-30 for RF) | Range for InCombat detection; monsters beyond this are tracked but don't trigger combat |
| `BacktrackLootThreshold` | 50c (plan config) | Minimum chaos value to justify backtracking for behind loot |
| Forward grab radius | 25 grid (hardcoded) | Items within this distance are always grabbed |

## Known Limitations

- **Ritual shop**: purchasing is implemented in StackedDeckStrategy but the WaveFarm AlchAndGoPlan has no post-clear actions. The TODO placeholder exists in WaveFarmMode.TickInMap but is commented out.
- **ThreatMap LeftRange monsters**: When monsters leave the network bubble alive, ThreatMap marks them LeftRange but keeps their alive count. If they were actually killed by another player or despawned, the chunk overestimates alive count until the player revisits and reconciliation runs.
- **Loot pickup rate**: currently ~28% in testing. The debug logging added should help diagnose whether items are failing at the scan stage (not in candidates) or the interaction stage (click failures).
