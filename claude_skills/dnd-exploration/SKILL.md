---
name: dnd-exploration
description: Travel, rest, search and discovery, traps, encounters and their cleanup, and where a scene stands in the location hierarchy
metadata:
  type: skill
---

# Exploration

Travel, rest, search, encounters, and where a scene stands. Seeding new places is `dnd-world-building`; change syntax is `dnd-world-change`.

## Where a scene stands

Locations form a hierarchy through `type` and `parentLocationId`: Region → Settlement → District → Building → Room. `Wilderness` sits outside the settlement chain (a clearing, a ravine, a cave mouth) at whatever scale the region needs.

- **Region and Settlement** are backdrops for arriving and leaving ("the free city sprawls below"), never where an active scene stands.
- **Districts** are named neighbourhoods and streets. Create them freely: they give the party somewhere concrete to be before any interior exists.
- **Buildings and Rooms** are where scenes that last more than a beat play out: a particular tavern, the captain's office, the alley behind the smithy.

Before a conversation, search or fight in a settlement, resolve or create the District, Building or Room it happens in. Set `connectedFromLocationId` and `connectionDescription` so the new place links to its parent:

```json
{
  "locations": [
    { "id": "locations/dockside", "name": "Dockside", "type": "District", "parentLocationId": "locations/neverwinter", "connectedFromLocationId": "locations/neverwinter", "connectionDescription": "The harbor gate opens onto the docks." },
    { "id": "locations/salty-anchor", "name": "The Salty Anchor", "type": "Building", "parentLocationId": "locations/dockside", "connectedFromLocationId": "locations/dockside", "connectionDescription": "A weathered tavern facing the pier." }
  ]
}
```

A spot that matters only this beat (a hiding place, a ledge inside the room) lives in the description. If the party will touch, take or come back to it, make it real: a fixture is an item held by the location (its contents are items held by that item), and a place they can enter is a child location with exits. There are no points of interest.

## Moving

- **`activity`**: a local move inside the same safe place. No encounter check, no need costs. `newActivity` alone repositions someone (the corner, behind the bar); `newLocationId` with `updateLocation: true` is only for moving to a different location that already exists.
- **`travel`**: a real journey (distance, alone, hostile or unknown ground). It rolls encounter risk (`encounterRiskModifier`), costs needs, and can be interrupted. Treacherous conditions (fog, storm, a marsh at night) go in its `hazard`: on journeys outside town a companion may get separated, which the engine reports as `SEPARATED:` with where they are; play the reunion.
- **`rest`**: a night or part of a day with real danger. It rolls interruptions and recovers pools and tiredness at once, but only if it completes.
- **`advance_world`**: a multi-day skip (`dnd-campaign-events`).

Leaving for a distinct spot (an hour into the woods, off the road to camp) means creating it as a child location in the same batch and traveling into it, never stapling the spot onto a broad Region or Wilderness, which misreports who is present and has no exits or danger of its own:

```json
{
  "changes": [
    { "$type": "location_update", "locationId": "locations/hidden-hollow", "name": "Hidden Forest Hollow", "type": "Wilderness", "description": "A secluded hollow a kilometer into the trees, screened from the road.", "parentLocationId": "locations/sword-coast", "dangerModifier": -10, "addExit": { "targetLocationId": "locations/sword-coast", "description": "Back toward the High Road" } },
    { "$type": "travel", "characterId": "chars/lyra", "destinationLocationId": "locations/hidden-hollow", "travelCostHoursOverride": 0.5, "encounterRiskModifier": -15 }
  ],
  "narrative": "Lyra leaves the road for a hidden hollow to camp in."
}
```

`location_update` on a new id creates the place: `name` is required, and set `type` (it defaults to Room). With the campaign's default connectivity repair, the exit back is added on the other side automatically.

## Arriving

1. Put `fullDetailLocationId` on the same `take_turn` as the travel. `fullScene` brings the place, the people present and its plot threads; don't spend a separate `get_entity`.
2. Plot threads there: a Dormant one gets one foreshadowing hook woven in, an Active one surfaces a clue or a hint, a Climax one brings immediate consequences.
3. `fullScene.scenePressure` may carry ENGINE WARNINGs for this place (missing clue entities, a place not yet seeded): fix them per `dnd-campaign-events`.
4. The first arrival of a session also brings `fullScene.dmOnly`: every secret the place holds. Foreshadow from it; never read it out.

## Searching and traps

A search is a `ruleset_action` SkillCheck (Perception, Investigation, Survival) with a `dc`. Don't invent what they find: the engine checks the total against the place's secrets (hidden exits, concealed items, secret details, traps) and reports each `FOUND:`; arrivals get the same from passive Perception as `NOTICED:`. Those, and only those, are what the party finds.

To disarm a trap, add `"disarm": "<trap name>"` to a check's parameters (Thieves' Tools or Sleight of Hand, `dc` = its disarm DC). The result is `DISARMED:`, still armed, or on a bad miss it goes off. A `HAZARD:` line means a trap fired: roll the save it names with a `ruleset_action`, then commit the result.

Event categories for these beats: `Arrival`, `Discovery`, `Travel`, `SceneInterrupt`, `Timeskip`. There is no `Exploration` category, and a wrong one fails the whole batch.

## Encounters

Travel, rest and `scene_interrupt_check` can spawn an encounter: the engine returns the NPC or creature, and you run what follows. A spawned NPC is really there: resolve them in the story or say why they are gone, never sleep through a live encounter.

An encounter NPC is a blank sheet until you write one. Decide who they are and what they want (from the place and what they can see of the PC) before any check, never afterwards to explain a roll.

**Clear people who are no longer there.** Someone the roster lists but the story has put hours away or sent off is not on stage: clear them rather than voicing them. The same goes for a transient once the encounter resolves, since `keepAlive: false` NPCs stay in the roster until cleared: in the batch that resolves it, commit an `activity` with `newLocationId: null` and `updateLocation: true`. To keep them instead (a recurring threat), `character_update` with `keepAlive: true`, never both. A death is a `death` change.

## Rests

- The interruption roll runs per 4-hour stretch. If it fires, the clock moves only by the hours slept, nothing recovers, and an encounter arrives. The rest isn't done and the night hasn't passed: resolve the encounter, then commit a new `rest` for the remaining hours (or `advance_world`) before narrating morning. An `event` saying "morning" with `minutesElapsed` does not finish a rest.
- The engine can't see a watch, a garrison or a Tiny Hut. When someone really keeps watch, set a positive `securityModifier` (+20 for a hidden camp, +100 for a Tiny Hut; negative for exposed); otherwise the rest rolls the place's raw danger. A wilderness node stays dangerous however many NPCs are narrated nearby.
- Short travel (2–4 hours) ticks needs a little, long travel (8+) a lot.

## Checklist

- [ ] The scene stands in a District, Building or Room, not a Settlement or Region.
- [ ] A local move is `activity`; a journey is `travel`; a dangerous night is `rest`.
- [ ] The travel call carried `fullDetailLocationId`.
- [ ] Finds came from the engine's `FOUND:` and `NOTICED:` lines.
- [ ] Resolved encounter NPCs were cleared or kept in the same batch.
- [ ] No morning was narrated after an interrupted rest.
