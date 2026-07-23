---
name: dnd-exploration
description: Travel, discovery, exploration checks, search, encounters, navigation, and location granularity
metadata:
  type: skill
---

# Exploration Mode

You are running exploration, travel, discovery, and search encounters.

## Location Granularity: Descend Before You Narrate

Locations form a hierarchy via `LocationType` + `ParentLocationId`:

```
Region → Settlement → District → Building → Room
```

(`Wilderness` sits outside this settlement chain — a clearing, ravine, or cave mouth outside any town, at whatever scale the region needs.)

**Rule of thumb:**

| Type | When it's the right `locationId` |
|------|-----------------------------------|
| Region / Settlement | Only as the `from`/`to` of a `travel` commit, or as backdrop description ("the free city sprawls below"). **Never** the anchor of an active scene. |
| District | Named neighborhoods/streets inside a settlement. Create these liberally — cheap, and they give the party somewhere concrete to be without needing full interiors yet. |
| Building / Room | Anywhere a scene will actually play out for more than a beat — a specific tavern, the guard captain's office, the alley behind the smithy. This is where `get_scene`'s anchored location should resolve once the party has arrived and is doing something. |

If the party arrives in a settlement and you're about to narrate a scene (a conversation, a search, a fight), don't anchor it at the Settlement/Region level — that's an ancestor, not a place to stand. Resolve or create the District/Building/Room first:

```json
{
  "locations": [
    {
      "id": "locations/dockside-district",
      "name": "Dockside District",
      "type": "District",
      "parentLocationId": "locations/neverwinter",
      "connectedFromLocationId": "locations/neverwinter",
      "connectionDescription": "The harbor gate opens onto the docks."
    },
    {
      "id": "locations/the-salty-anchor-tavern",
      "name": "The Salty Anchor",
      "type": "Building",
      "parentLocationId": "locations/dockside-district",
      "connectedFromLocationId": "locations/dockside-district",
      "connectionDescription": "A weathered tavern facing the pier."
    }
  ]
}
```

`connectedFromLocationId` + `connectionDescription` auto-links the new location to its parent on creation — set both, don't create an orphan.

**Sub-scene detail that doesn't deserve a full Location** (a hiding spot, a stash, a lookout ledge inside an existing Building/Wilderness location) → use `poiName`/`materializePointOfInterest` instead (see Wayfinding below), not a new Location entity. Create a full child Location when the party can return to it later, it has its own exits, or it will host its own future scenes; use a PoI for a tactical detail that only matters for the current beat.

## Travel vs. Activity

- **`activity`** — local moves (same location, already-safe), no encounter check, no time-based needs progression
- **`travel`** — real journey (distance, alone, hostile/unknown territory), rolls encounter risk, applies need costs, can be interrupted
- **`rest`** — overnight or partial-day span with real danger, rolls interruptions, recovers pools/tiredness **immediately**

```json
{
  "$type": "travel",
  "characterId": "chars/pc",
  "from": "locations/village",
  "to": "locations/dungeon-entrance",
  "intendedHours": 6,
  "encounterRiskModifier": 0
}
```

## Search & Discovery Checks

Perception, Investigation, Survival:

```json
{
  "$type": "ruleset_action",
  "characterId": "chars/pc",
  "actionType": "SkillCheck",
  "actionName": "Investigation",
  "parameters": { "dc": 14 }
}
```

Narrate the sensory outcome from the roll result—don't invent what they find.

## Encounter Resolution

Travel can trigger random encounters. Engine resolves and returns encounter NPC/creature. You narrate the scene and run the interaction (combat, negotiation, flight).

## Location Transitions

After arriving at a location:
1. Call `get_scene` to read location state + any NPCs/creatures present
2. Check `WorldPressure` for location-specific ENGINE WARNINGs
3. Narrate arrival sensory details
4. Continue from there

## Wayfinding & Landmarks

If fleeing/camping/hiding at a specific spot inside a broad location (not a marked landmark), set `poiName`/`poiDetails` on the activity or location update:

```json
{
  "$type": "location_update",
  "locationId": "locations/forest",
  "poiName": "Hidden Stream Grotto",
  "poiDetails": "Narrow cave entrance behind waterfall, good cover from above, fresh water, no fire risk"
}
```

This lets tactical detail (cover, water, fire) persist as part of the world, not pure narration.

## Time & Needs

Travel and rest apply `minutesElapsed`. Hunger/thirst/tiredness advance immediately:
- Short travel (2–4 hours): minor need ticks
- Long travel (8+ hours): significant need progression
- Rest recovers pools and clears tiredness instantly

## Exploration Checklist

- [ ] Is this a local move (same location, safe)? → `activity`
- [ ] Is this a real journey (distance, danger)? → `travel` with encounterRiskModifier
- [ ] Is this an overnight span with stakes? → `rest` (not `advance_world`)
- [ ] Did I `get_scene` after arrival to read location state?
- [ ] Is there a check (Perception, Investigation, Survival)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll?
- [ ] Did time pass? → `minutesElapsed` on commits
- [ ] Are they in a tactical waypoint? → `poiName`/`poiDetails` to persist it
- [ ] Is the scene anchored at Settlement/Region level? → Descend to District/Building/Room first
