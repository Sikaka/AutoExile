# Simulacrum Mode Flowchart

## Phase State Machine (Top Level)

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> InHideout : OnEnter (in hideout/town)
    Idle --> FindMonolith : OnEnter (in map)

    state "HIDEOUT LOOP" as hideout {
        InHideout --> StashItems : has inventory items
        InHideout --> OpenMap : inventory empty
        StashItems --> OpenMap : stash done/failed
        OpenMap --> EnterPortal : map device succeeded
        OpenMap --> OpenMap : retry on failure (10s)
    }

    EnterPortal --> FindMonolith : area changed to map
    EnterPortal --> InHideout : 15s timeout (no portal)

    state "MAP LOOP" as map {
        FindMonolith --> NavigateToMonolith : monolith found
        FindMonolith --> Done : 30s timeout
        NavigateToMonolith --> WaveCycle : near monolith OR wave already active
        WaveCycle --> BetweenWaveStash : inv >= threshold & between waves
        BetweenWaveStash --> WaveCycle : stash done/failed/wave started
        WaveCycle --> LootSweep : wave 15 complete OR wave timeout
        LootSweep --> ExitMap : sweep done (5s empty) OR 60s timeout
        ExitMap --> Done : 30s timeout
    }

    ExitMap --> InHideout : area changed to hideout (map completed)
    ExitMap --> InHideout : area changed to hideout (too many deaths)
    ExitMap --> EnterPortal : area changed to hideout (death, under limit)
    Done --> [*]
```

## Area Change Handler

```mermaid
flowchart TD
    AC[Area Changed] --> CANCEL[Cancel MapDevice + Stash + Interaction]
    CANCEL --> IS_HO{Hideout/Town?}

    IS_HO -->|Yes| MAP_DONE{mapCompleted?}
    MAP_DONE -->|Yes| NEW_RUN[RecordRunComplete + Reset<br/>phase = InHideout]
    MAP_DONE -->|No| DEATH_CHK{deaths > 0 AND<br/>deaths < maxDeaths?}
    DEATH_CHK -->|Yes| REENTER[phase = EnterPortal<br/>Re-enter map]
    DEATH_CHK -->|No| TOO_MANY{deaths >= maxDeaths?}
    TOO_MANY -->|Yes| FRESH[RecordRunComplete + Reset<br/>phase = InHideout]
    TOO_MANY -->|No| IDLE_HO[phase = InHideout]

    IS_HO -->|No: Entered Map| ENTER_MAP[OnAreaChanged preserves deathCount<br/>phase = FindMonolith<br/>lootPickupCount = 0]
```

## WaveCycle Decision Loop (Core Logic)

```mermaid
flowchart TD
    TICK[TickWaveCycle] --> LOOT_RESULT[HandleLootResult<br/>Record success / mark failed]
    LOOT_RESULT --> WAVE_CHG{wave number changed?}
    WAVE_CHG -->|Yes| RESET_SEEN[ResetSeen exploration<br/>Clear patrol state]
    WAVE_CHG -->|No| P1
    RESET_SEEN --> P1

    P1{P1: Wave active<br/>AND loot nearby?}
    P1 -->|Yes| PICKUP_ACTIVE[PickupNext item<br/>Track pending loot]
    P1 -->|No| P2

    P2{P2: Wave active AND<br/>wave timer > timeout?}
    P2 -->|Yes| TIMEOUT[phase = LootSweep<br/>Sweep loot before exit]
    P2 -->|No| P3

    P3{P3: Wave active?}
    P3 -->|Yes| CHASE_CHK{NearbyChaseCount > 0?<br/>monsters within 80 grid}
    P3 -->|No| P4

    CHASE_CHK -->|Yes| WAS_SEARCH{wasSearching?}
    WAS_SEARCH -->|Yes| RESET_SEEN2[ResetSeen exploration<br/>wasSearching = false]
    WAS_SEARCH -->|No| FIGHT
    RESET_SEEN2 --> FIGHT[Record spawn zone<br/>Reset patrol<br/>Combat handles fighting]
    CHASE_CHK -->|No| SEARCH[wasSearching = true<br/>TickExploreForMonsters]

    P4{P4: Between waves<br/>loot nearby OR picking up?}
    P4 -->|Yes| BETWEEN_LOOT[PickupNext / wait<br/>Reset wave delay timer<br/>Idle at spawn zone]
    P4 -->|No| P5

    P5{P5: Stash position known<br/>AND inv >= threshold?}
    P5 -->|Yes| STASH[phase = BetweenWaveStash]
    P5 -->|No| P6

    P6{P6: Wave >= 15<br/>AND not active?}
    P6 -->|Yes| SWEEP[phase = LootSweep]
    P6 -->|No| P7

    P7{P7: CanStartWaveAt passed<br/>AND wave < 15?}
    P7 -->|Yes| START_WAVE[TickStartWave<br/>Navigate to monolith + click]
    P7 -->|No| WAIT[Idle at spawn zone<br/>Wait for delay timer]
```

## TickExploreForMonsters (Monster Search)

```mermaid
flowchart TD
    EFM[TickExploreForMonsters] --> HAS_SPAWN{HasSpawnData?}

    HAS_SPAWN -->|Yes| HAS_TARGET{Has patrol target?}
    HAS_SPAWN -->|No| DISTANT_CHK

    HAS_TARGET -->|No| PICK_ZONE[Pick farthest spawn zone]
    PICK_ZONE --> HAS_TARGET
    HAS_TARGET -->|Yes| ZONE_DIST{dist > 25 grid?}

    ZONE_DIST -->|Yes, far| NAV_ZONE[Navigate to spawn zone]
    ZONE_DIST -->|No, arrived| WAIT_CALC{NearbyMonsterCount > 0?}

    WAIT_CALC -->|Yes| MAX8[maxWait = 8s]
    WAIT_CALC -->|No, zero monsters| MAX1[maxWait = 1s]
    MAX8 --> IDLE_CHK{idleTime < maxWait?}
    MAX1 --> IDLE_CHK

    IDLE_CHK -->|Yes| WAIT_ZONE[Stop nav, wait at zone]
    IDLE_CHK -->|No, stale| STALE_MONSTER{NearbyMonsterCount > 0?}

    STALE_MONSTER -->|Yes| CLEAR_PATROL[Clear patrol target<br/>Fall through to chase]
    STALE_MONSTER -->|No| CYCLE_CHK{Visited all zones<br/>this cycle?}

    CYCLE_CHK -->|Yes| CLEAR_CYCLE[Clear patrol + counter<br/>Fall through to explore]
    CYCLE_CHK -->|No| NEXT_ZONE[Pick next spawn zone<br/>Increment visit counter]

    CLEAR_PATROL --> DISTANT_CHK
    CLEAR_CYCLE --> DISTANT_CHK

    DISTANT_CHK{NearbyMonsterCount > 0?<br/>distant alive monsters}
    DISTANT_CHK -->|Yes| CHASE_PACK[Navigate to PackCenter<br/>center of ALL alive monsters]
    DISTANT_CHK -->|No| NAV_ACTIVE{Already navigating?}

    NAV_ACTIVE -->|Yes| SEARCHING[Status: searching]
    NAV_ACTIVE -->|No| EXPLORE{Exploration initialized<br/>AND has unseen target?}

    EXPLORE -->|Yes| NAV_EXPLORE[Navigate to explore target]
    EXPLORE -->|No| MONO_CHK{Monolith position known?}

    MONO_CHK -->|Yes| MONO_DIST{dist to monolith?}
    MONO_CHK -->|No| NO_TARGETS[Status: searching no targets]

    MONO_DIST -->|> 80 grid| RETURN_MONO[Navigate to monolith]
    MONO_DIST -->|< 30 grid| ORBIT[Orbit monolith at ~50 grid<br/>time-varying angle]
    MONO_DIST -->|30-80 grid| NO_TARGETS
```

## LootSweep (Post-Wave/Timeout)

```mermaid
flowchart TD
    LS[TickLootSweep] --> LR[HandleLootResult]
    LR --> LS_TO{> 60s elapsed?}
    LS_TO -->|Yes| EXIT[EnterExitMapPhase]
    LS_TO -->|No| BUSY{Interaction busy?}
    BUSY -->|Yes| WAIT_LS[Wait for pickup]
    BUSY -->|No| STASH_CHK{Stash available<br/>AND inv > 0?}
    STASH_CHK -->|Yes| STASH_LS[Stash items first]
    STASH_CHK -->|No| SCAN[Scan for loot]
    STASH_LS --> SCAN
    SCAN --> FOUND{Candidate found?}
    FOUND -->|Yes| PICK[Start pickup<br/>Reset empty timer]
    FOUND -->|No| GRACE{Empty scan timer<br/>started?}
    GRACE -->|No| START_TIMER[Start 5s grace timer]
    GRACE -->|Yes, < 5s| WAIT_GRACE[Wait for more drops]
    GRACE -->|Yes, >= 5s| EXIT
```

## Key State Transitions Summary

| From | To | Trigger |
|------|-----|---------|
| InHideout | StashItems | Has inventory items |
| InHideout | OpenMap | Inventory empty |
| StashItems | OpenMap | Stash succeeded/failed |
| OpenMap | (area change) | MapDevice portal entered |
| FindMonolith | NavigateToMonolith | Monolith entity found |
| NavigateToMonolith | WaveCycle | Within 18 grid of monolith OR wave already active |
| WaveCycle | LootSweep | Wave timeout OR wave 15 complete |
| WaveCycle | BetweenWaveStash | Between waves, inv >= threshold |
| BetweenWaveStash | WaveCycle | Stash done/failed OR wave started |
| LootSweep | ExitMap | 5s no loot OR 60s timeout |
| ExitMap | (area change) | Portal clicked, enter hideout |
| (area change to hideout) | InHideout | mapCompleted OR too many deaths |
| (area change to hideout) | EnterPortal | Death under limit |
| (area change to map) | FindMonolith | Always |

## Exploration Reset Points

| When | Why |
|------|-----|
| Wave number changes | New wave = new monster spawns in visited areas |
| Search → Combat transition | Found monsters after searching; next search needs fresh sweep |
| `ResetSeen()` clears SeenCells + FailedRegions | Blob/region structure preserved, only visibility reset |
