---
name: dnd-campaign-events
description: Quests, rumors, factions, pressures, time advancement, and campaign-level events
metadata:
  type: skill
---

# Campaign-Events Mode

You are managing campaign-level state: quests, rumors, factions, pressures, and world time.

## World Pressure (ENGINE WARNINGs)

When you call `get_world_state` or `get_scene`, check `WorldPressure` immediately. If there's an `ENGINE WARNING`, resolve it atomically **before continuing**:

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

Commit the suggested resolution in the same batch. Do not skip or defer ENGINE WARNINGs. 5+ unresolved warnings cap progress; call `get_help topic=pressure` to drain the backlog.

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

Create new rumors via `world_build` (batch). Evolve existing rumors via `commit`.

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

## Plot Thread Progression

Track multi-beat narrative arcs:

```json
{
  "$type": "plot_thread_progress",
  "plotThreadId": "plot/conspiracy-within-council",
  "newState": "Escalating",
  "clue": "council-member-letter-intercepted"
}
```

## Campaign Time

Campaign has a clock: `get_world_state` returns current campaign time (day, hour, weather, season). Most commits accept `minutesElapsed` to tick the clock. Use `advance_world` for larger skips.

## Pressure-Driven Pacing

Read `WorldPressure` after every major scene:
- **Low pressure** → party can breathe, plan, recover
- **Rising pressure** → multiple unresolved nags, stakes climbing
- **Peak pressure** → faction moves, quest deadlines, weather shifts, ENGINE WARNINGs escalate

Use pressure as a narrative cue: when pressure peaks, events accelerate.

## Campaign Checklist

- [ ] Did I call `get_world_state` to read campaign time + pressure?
- [ ] Are there ENGINE WARNINGs? → Resolve atomically before continuing
- [ ] Did a quest milestone complete? → `quest_progress` commit
- [ ] Did the party's relationship with a faction shift? → `faction_state`
- [ ] Did significant time pass (hours/days)? → `advance_world` or `minutesElapsed` on commits
- [ ] Did a rumor evolve? → `rumor` commit with newState
- [ ] Did a plot thread escalate? → `plot_thread_progress`
- [ ] Is pressure climbing? → Narrate mounting stakes, escalate NPC actions
