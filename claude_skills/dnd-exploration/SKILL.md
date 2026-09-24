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
| Region / Settlement | Only as travel origin/destination backdrop ("the free city sprawls below"). **Never** the anchor of an active scene. |
| District | Named neighborhoods/streets inside a settlement. Create these liberally — cheap, and they give the party somewhere concrete to be without needing full interiors yet. |
| Building / Room | Anywhere a scene will actually play out for more than a beat — a specific tavern, the guard captain's office, the alley behind the smithy. This is where the anchored scene (get_entity with the location id) should resolve once the party has arrived and is doing something. |

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

**Sub-scene detail that doesn't deserve a full Location** (a hiding spot, a stash, a lookout ledge inside an existing Building/Wilderness location) → narrate it from the description; don't create an entity for a detail that only matters this beat. If the party will touch, take or return to it, make it real: a fixture or object is an item held by the location (contents are items held by that item), and a place they can enter or come back to is a child Location with exits. There are no points of interest.

## Seeding a New Area? Load `dnd-world-building`

The full world-building seeding checklist (settlement/district/building/exit depth, fixtures and secrets, plot thread enrichment, clue materialization, item templates) now lives in the **`dnd-world-building`** skill — load it before any `world_build` call that seeds a new area (session 0, arrival in a new settlement, entering a new region) or a new plot thread. This skill (`dnd-exploration`) covers the location-hierarchy model that checklist assumes (Region → Settlement → District → Building → Room, above), plus in-play navigation, search, and encounters — not the seeding process itself.

## Travel vs. Activity

- **`activity`** — local moves (same location, already-safe), no encounter check, no time-based needs progression
- **`travel`** — real journey (distance, alone, hostile/unknown territory), rolls encounter risk, applies need costs, can be interrupted
- **`rest`** — overnight or partial-day span with real danger, rolls interruptions, recovers pools/tiredness **immediately**
- **`advance_world`** — multi-day/uneventful skip. Pass its `partyLocationId` param to get the same encounter/ambient-crowd checks `rest`/`travel` roll, scaled to the elapsed span; omit it only for a skip that's genuinely meant to carry zero risk.

```json
{
  "$type": "travel",
  "characterId": "chars/pc",
  "destinationLocationId": "locations/dungeon-entrance",
  "travelCostHoursOverride": 6,
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

Narrate the sensory outcome from the roll result—don't invent what they find. The engine checks the total against the location's secrets (hidden ways, concealed items, secret details, traps) and reports each `FOUND:` in the summary; arrivals get the same from passive Perception (`NOTICED:`). Narrate exactly those. To disarm a trap, add `"disarm": "<trap name>"` to a check's parameters (Thieves' Tools / Sleight of Hand, `dc` = its disarm DC): `DISARMED:`, still armed, or on a bad miss it goes off. A `HAZARD:` line means a trap fired: roll the save it names with `ruleset_action`, then commit the result.

Treacherous travel (fog, storm, a night marsh): put it in the travel change's `hazard`. On out-of-town legs it gives a rare chance that a companion loses the party; the engine reports `SEPARATED:` with where they are, so play the reunion rather than assuming it.

## Encounter Resolution

Travel can trigger random encounters. Engine resolves and returns encounter NPC/creature. You narrate the scene and run the interaction (combat, negotiation, flight).

**Blank sheet until you write one — decide who the NPC is *before* resolving the check** (from location flavor + PC's visible state), never retconning motive to justify a roll (detail: `dnd-social`'s Method-Not-Job).

**Clear the transient once the encounter resolves** — `keepAlive: false` NPCs linger in `PresentNPCs` until their `CurrentLocationId` is cleared (engine eviction is day-granularity). Same batch as the resolution narration: `activity` with `newLocationId: null` + `updateLocation: true`. To promote instead (recurring threat), `character_update` with `keepAlive: true` — don't do both.

## Location Transitions & Plot Threads

After arriving at a location:
1. Put `fullDetailLocationId` on the same `take_turn` that commits the travel: `fullScene` returns location state, NPCs/creatures present and `associatedPlotThreads`. Don't spend a separate `get_entity` call on a room you're entering.
2. Check `associatedPlotThreads`: Dormant → weave one foreshadowing hook; Active → surface a clue/NPC hint; Climax → immediate consequences
3. Check `fullScene.scenePressure` for location-specific ENGINE WARNINGs (missing clue entities, unvisited transients) — fix per `dnd-campaign-events`
4. Narrate arrival sensory details

**Lazy Seeding on Arrival:** If the location or its parent district/building isn't yet seeded, an ENGINE WARNING will nudge you — seed it (`dnd-world-building` checklist) before continuing.

## Wayfinding & Landmarks

Two different moves depending on how far/long/exposed the departure is — don't default to the lighter one just because it's a single field:

**Staying inside the current location** (corner, behind the bar, alcove, cot) — `newActivity` alone repositions; it needs no `newLocationId`/`updateLocation` when the location document isn't changing. Reserve those for a genuine transition to a *different*, already-existing `Location` (invented ids are rejected, not accepted). Lasting physical detail → `location_update` (state, tags, features) or an `item_update` detail on the fixture, in the same batch.

**Leaving to a real, distinct spot** (an hour into the woods, off the road to make camp) — `location_update` create-and-link a real child `Location` in the same batch, then `travel`/`activity` into it. Never staple a distinct spot onto a broad Region/Wilderness (misreports who's "present" and gets no tuned exits/danger):

```json
{
  "changes": [
    {
      "$type": "location_update",
      "locationId": "locations/sword-coast-hidden-hollow",
      "name": "Hidden Forest Hollow",
      "description": "A secluded hollow a kilometer into the trees, screened from the road.",
      "type": "Wilderness",
      "parentLocationId": "locations/sword-coast",
      "dangerModifier": -10,
      "addExit": { "targetLocationId": "locations/sword-coast", "description": "Back toward the High Road" }
    },
    {
      "$type": "travel",
      "characterId": "chars/lyra",
      "destinationLocationId": "locations/sword-coast-hidden-hollow",
      "travelCostHoursOverride": 0.5,
      "encounterRiskModifier": -15
    }
  ],
  "narrative": "Lyra leaves the road, picking a hidden hollow to camp for the night."
}
```

`location_update` on a `locationId` that doesn't exist yet creates it (requires `name`; `type` defaults to `Room` if omitted — set it explicitly for outdoor spots). Pair `parentLocationId` with `addExit` back to where the party came from — with `autoRepairLocationConnectivity` on (the campaign default), the reverse exit is added on the target automatically, so both `travel` (this call) and a future return trip resolve correctly. Encounters remain possible both ways: `encounterRiskModifier` on the `travel` itself, and `securityModifier` on a follow-up `rest` change for risk while lingering there.

## Time & Needs

Travel and rest advance time via their own hour fields (not `minutesElapsed` — that's for other changes, set on the top-level `take_turn` request). Hunger/thirst/tiredness advance immediately:
- Short travel (2–4 hours): minor need ticks
- Long travel (8+ hours): significant need progression
- Rest recovers pools and clears tiredness instantly

## Exploration Checklist

**During play:**
- [ ] Is this a local move (same location, safe)? → `activity`
- [ ] Is this a real journey (distance, danger)? → `travel` with encounterRiskModifier
- [ ] Is this an overnight span with stakes? → `rest`, or `advance_world` with `partyLocationId` set so it still rolls for interruptions
- [ ] Did the travel `take_turn` carry `fullDetailLocationId` for the destination?
- [ ] Is there a check (Perception, Investigation, Survival)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll?
- [ ] Did time pass? → `minutesElapsed` on the request (rest/travel use their own hour fields instead)
- [ ] Is the scene anchored at Settlement/Region level? → Descend to District/Building/Room first
- [ ] Did an encounter/`scene_interrupt_check` NPC just resolve (left, dealt with, party moved on)? → `activity` change clearing their `CurrentLocationId` in the same batch as the resolution narration

**When seeding a new area (world_build):** load `dnd-world-building` and run its checklist before calling `world_build`.
