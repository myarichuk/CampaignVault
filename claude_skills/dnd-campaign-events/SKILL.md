---
name: dnd-campaign-events
description: Quests, rumors, factions, pressures, time advancement, and campaign-level events
metadata:
  type: skill
---

# Campaign-Events Mode

You are managing campaign-level state: quests, rumors, factions, pressures, and world time.

## World Pressure (ENGINE WARNINGs)

Whenever a response carries `WorldPressure` (start_session, a scene fetch via get_entity, take_turn with includeWorldState, advance_world), check it immediately. If there's an `ENGINE WARNING`, resolve it atomically **before continuing**:

```json
// Example ENGINE WARNING
{
  "severity": "WARNING",
  "text": "Rumor about bandits is stale; faction morale is low",
  "suggestedResolve": {
    "$type": "rumor",
    "rumorId": "rumor/bandits-growing",
    "newState": "Peak"
  }
}
```

Include the suggested resolution in the same take_turn batch. Do not skip or defer ENGINE WARNINGs. 5+ unresolved warnings cap progress; call `get_help topic=world-pressure` to drain the backlog.

## Quest Progress

Track quest milestones:

```json
{
  "$type": "quest_progress",
  "questId": "quest/find-the-missing-caravan",
  "newState": "Active",
  "discoveredClues": ["caravan-tracks-north", "torn-cargo-manifest"],
  "summary": "Party acquired the manifest from the trader; heading north to follow the tracks"
}
```

States: Open → Active → Complete → Failed (or Abandoned).

## Rumor Evolution

Rumors progress through lifecycle:

```json
{
  "$type": "rumor",
  "rumorId": "rumor/bandits-recruiting",
  "newState": "Spreading"
}
```

States: Nascent → Spreading → Peak → Fading → Resolved (or Forgotten).

Create new rumors via `world_build` (batch). Evolve existing rumors via a `rumor` change in `take_turn`.

## Faction State & Economy

Track faction stance changes:

```json
{
  "$type": "faction_state",
  "targetFactionId": "factions/thieves-guild",
  "newStance": "Hostile",
  "reason": "Party murdered guild courier"
}
```

Factions have `EconomicDemand` (items they want). If the party carries demanded items, `FactionEconomyPressureContributor` surfaces opportunities in `WorldPressure`.

## Time Advancement

Use `advance_world` to skip uneventful time (e.g., "three weeks pass peacefully"):

```json
{
  "campaignName": "<current-campaign>",
  "hours": 504,
  "skipEvaluateSchedules": false
}
```

This rolls simulation rules (needs, rumors, status expiry, NPC schedules) and returns `SimulatorEvents` + any pressures.

**Don't use `advance_world` for dangerous travel or overnight spans with stakes**—use `rest` (for immediate recovery + interruption rolls) or `travel` (for encounters).

## Plot Thread Progression & Scaffolding

Every plot thread seeded via `world_build` MUST include:

1. **`foreshadowingHooks` (2-4 strings):** Narratable teasers BEFORE the thread activates
   - Example: "A robed figure watching from a rooftop", "Overheard tavern rumor about strange shipments", "A letter found in a desk"
   - Weave these into scenes before plot activates; they prime the party for what's coming

2. **`clues` (2-4+ entries, each with id, description, involvedEntityIds):** Discoverable evidence DURING the thread
   - A scrap of paper, a witness statement, a tracking mark, a behavioral tic, a relationship dynamic
   - Each clue should be findable at a location or from an NPC
   - **Clue types:** physical (objects/locations), behavioral (NPC quirks/responses), relational (ties between NPCs/factions)

3. **`resolutionCondition` (testable end state):** Not "the party talks to them" but "party presents evidence of the camp to Maeva, and she calls off the war parties"

4. **`involvedEntityIds` (NPC IDs + related characters/factions):** At minimum the primary NPC the thread revolves around

**Clue materialization:** When a clue references a physical object, seed it via `world_build` as an `items[]` entry with `holderId` pointing to location/NPC. The clue's `involvedEntityIds` must include the item ID. Tag the item: `tags: ["clue:plot-threads/..."]`. Without this link, party searches find nothing.

**Reverse connections & validation:** Use `get_entity(plot-threads/...)` to fetch a thread. If ENGINE WARNING appears, the thread references missing entities (items/NPCs not yet seeded). Either seed them on-demand when plot demands, or remove stale clue references via `world_build`.

## Campaign Time

Campaign has a clock: `start_session` (and `take_turn` with `includeWorldState: true`) returns current campaign time (day, hour, weather, season). Most commits accept `minutesElapsed` to tick the clock. Use `advance_world` for larger skips.

## Pressure-Driven Pacing

Read `WorldPressure` after every major scene:
- **Low pressure** → party can breathe, plan, recover
- **Rising pressure** → multiple unresolved nags, stakes climbing
- **Peak pressure** → faction moves, quest deadlines, weather shifts, ENGINE WARNINGs escalate

Use pressure as a narrative cue: when pressure peaks, events accelerate.

## Campaign Checklist

- [ ] Did I read campaign time + pressure (start_session at kickoff; take_turn includeWorldState mid-play)?
- [ ] Are there ENGINE WARNINGs? → Resolve atomically before continuing
- [ ] Did a quest milestone complete? → `quest_progress` commit
- [ ] Did the party's relationship with a faction shift? → `faction_state`
- [ ] Did significant time pass (hours/days)? → `advance_world` or `minutesElapsed` on commits
- [ ] Did a rumor evolve? → `rumor` commit with newState
- [ ] Did a plot thread escalate? → `plot_thread_progress`
- [ ] Is pressure climbing? → Narrate mounting stakes, escalate NPC actions
