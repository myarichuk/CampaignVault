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

Include the suggested resolution in the same `take_turn` batch **and always pass `includeWorldState: true`** to verify the warning is resolved. After the response, **check `WorldPressure` again** — still listed means the fix didn't land; investigate, don't defer. Without `includeWorldState: true` the response carries no WorldPressure, so an unverified "fix" is unconfirmed. 5+ unresolved warnings cap progress; `lookup kind=help topic=world-pressure` drains the backlog.

```json
{
  "campaignName": "kael-quest",
  "request": {
    "changes": [ { "$type":"rumor", "rumorId":"rumor/bandits-growing", "newState":"Peak" } ],
    "narrative": "The rumor about bandits reached peak intensity in the community.",
    "includeWorldState": true
  }
}
```

## Quest Progress

Track quest milestones: `quest_progress.newState` is `Open` → `InProgress` → `Complete` / `Failed` / `Skipped`, plus `objectiveIndex`/`objectiveName` (required — fields: `dnd-world-change`).

## Rumor Evolution

Rumors progress through lifecycle:

```json
{
  "$type": "rumor",
  "rumorId": "rumor/bandits-recruiting",
  "newState": "Spreading"
}
```

States: Nascent → Spreading → Peak → Fading → Resolved (or Forgotten). New rumors are seeded via `world_build`; existing ones evolve via a `rumor` change in `take_turn` (on a delta turn only changed rumors resurface — an empty list means none changed, not that they died; full picture via `includeWorldState: true` / `get_entity`).

## Faction State & Economy

Track faction stance changes: `faction_state` with `factionId` (the subject) and `targetFactionId` only for a stance *toward* another faction (fields: `dnd-world-change`).

Factions have `EconomicDemand` (items they want). If the party carries demanded items, `FactionEconomyPressureContributor` surfaces opportunities in `WorldPressure`.

## Time Advancement

Use `advance_world` to skip uneventful time (e.g., "three weeks pass peacefully"):

```json
{
  "narrative": "Three uneventful weeks pass at the keep.",
  "campaignName": "<current-campaign>",
  "hours": 504
}
```

(There's no `skipEvaluateSchedules` param — `narrative` is required on every call.)

This rolls simulation rules (needs, rumors, status expiry, NPC schedules) and returns `SimulatorEvents` + any pressures. By itself it has **zero encounter/interruption mechanic** — pass `partyLocationId` to get the same encounter/ambient-crowd rolls `rest`/`travel` get for that elapsed span.

**For dangerous travel or an overnight span with real stakes**, prefer `rest` (immediate recovery + interruption rolls) or `travel` (encounters) — or `advance_world` with `partyLocationId` set if a multi-day skip still needs to carry risk.

## Plot Thread Progression & Scaffolding

Scaffolding fields are canonical in `dnd-world-building` (2–4 `foreshadowingHooks`, 2–4 `clues` with `id`/`description`/`involvedEntityIds`, testable `resolutionCondition`, `involvedEntityIds`). Progress in play via `plot_thread_progress`; escalate per pressure below.

**Clue materialization:** a clue referencing a physical object needs a matching `world_build` `items[]` entry (`holderId` set; clue's `involvedEntityIds` includes the item; item tagged `tags: ["clue:plot-threads/..."]`) — otherwise searches find nothing. **Validation:** `get_entity(plot-threads/...)`; an ENGINE WARNING means missing entities — seed on demand or drop the stale reference.

## Campaign Time

Campaign has a clock: `start_session` (and `take_turn` with `includeWorldState: true`) returns current campaign time (day, hour, weather, season). Set `minutesElapsed` on the top-level `take_turn` request to tick the clock (rest/travel use their own hour fields instead). Use `advance_world` for larger skips.

## Pressure-Driven Pacing

Read `WorldPressure` after every major scene:
- **Low pressure** → party can breathe, plan, recover
- **Rising pressure** → multiple unresolved nags, stakes climbing
- **Peak pressure** → faction moves, quest deadlines, weather shifts, ENGINE WARNINGs escalate

Use pressure as a narrative cue: when pressure peaks, events accelerate.

## Campaign Checklist (session tier — per-beat mechanics: `dnd-world-change`; prose: `dnd-narration`)

- [ ] Did I read campaign time + pressure (start_session at kickoff; take_turn includeWorldState mid-play)?
- [ ] Are there ENGINE WARNINGs? → Resolve atomically before continuing
- [ ] Did a quest milestone complete? → `quest_progress` commit
- [ ] Did the party's relationship with a faction shift? → `faction_state`
- [ ] Did significant time pass (hours/days)? → `advance_world` or `minutesElapsed` on the take_turn request
- [ ] Did a rumor evolve? → `rumor` commit with newState
- [ ] Did a plot thread escalate? → `plot_thread_progress`
- [ ] Is pressure climbing? → Narrate mounting stakes, escalate NPC actions
