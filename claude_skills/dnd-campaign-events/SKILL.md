---
name: dnd-campaign-events
description: Campaign-level state — ENGINE WARNINGs and world pressure, quests, rumors, factions, plot-thread progress, and skipping time with advance_world
metadata:
  type: skill
---

# Campaign Events

Quests, rumors, factions, plot threads, pressure and the passing of days. Change syntax is `dnd-world-change`; seeding them is `dnd-world-building`.

## ENGINE WARNINGs come first

`WorldPressure` arrives on `start_session`, on a location's `fullScene.scenePressure`, on `take_turn` with `includeWorldState: true`, and on `advance_world`. An ENGINE WARNING is resolved before the story moves on:

1. Put its suggested fix into the `take_turn` you are already sending for this beat, never a call of its own.
2. Add `includeWorldState: true` to that call; without it the response carries no pressure, so the fix is unconfirmed.
3. Check `WorldPressure` in the response. A warning still listed means the fix didn't land: find out why now.

Five or more unresolved warnings hold progress back, so clear the backlog before anything else.

```json
{
  "campaignName": "kael-quest",
  "request": {
    "changes": [ { "$type": "rumor", "rumorId": "rumors/bandits-growing", "newState": "Peak" } ],
    "narrative": "Talk of the bandits is everywhere in the market now.",
    "includeWorldState": true
  }
}
```

Pressure also paces the story: low pressure lets the party breathe and plan, rising pressure stacks unresolved problems, and at the peak factions move, deadlines land and the weather turns.

## Quests, rumors, factions, plot threads

- **Quests:** `quest_progress` with `newState` (Open, InProgress, Complete, Failed, Skipped) and `objectiveIndex` or `objectiveName`.
- **Rumors** move through Nascent, Spreading, Peak, Fading, Resolved (or Forgotten) with a `rumor` change; new ones come from `world_build`. On a delta turn only changed rumors come back; an empty list means none changed.
- **Factions:** `faction_state` (`factionId` is the subject, `targetFactionId` only for a stance toward another faction); `faction_reputation` for the party's standing. A faction's `EconomicDemand` lists goods it wants, and carrying them shows up as opportunities in `WorldPressure`.
- **Plot threads** advance with `plot_thread_progress` and `plot_thread_clue`. `get_entity` on the thread validates it; a warning about missing entities means seed them or drop the stale reference.

## Skipping time

`advance_world` skips uneventful time: `hours`, or `days` with `resultingHour`, and a `narrative` (always required).

```json
{ "campaignName": "kael-quest", "hours": 504, "narrative": "Three quiet weeks pass at the keep." }
```

It runs needs, rumors, status expiry and schedules, and returns what happened. On its own it rolls no encounters: pass `partyLocationId` to roll them for the elapsed span, and leave it out only when the skip really carries no risk. For one dangerous night or journey, `rest` or `travel` (`dnd-exploration`) fit better.

## Checklist

- [ ] ENGINE WARNINGs resolved in the current beat and verified with `includeWorldState`.
- [ ] Quest milestones, rumor changes, faction shifts and plot-thread steps committed when they happen.
- [ ] Long skips go through `advance_world`, with `partyLocationId` unless risk-free.
