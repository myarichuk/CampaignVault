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

**Sub-scene detail that doesn't deserve a full Location** (a hiding spot, a stash, a lookout ledge inside an existing Building/Wilderness location) → use `poiName`/`materializePointOfInterest` instead (see Wayfinding below), not a new Location entity. Create a full child Location when the party can return to it later, it has its own exits, or it will host its own future scenes; use a PoI for a tactical detail that only matters for the current beat.

## World-Building Seeding Checklist (Mandatory Rigor)

When seeding a new area (session 0, arrival in a new settlement, entering a new region), apply these layers in order. **Any missing layer is a gap** — the party should navigate the world at this resolution without the GM inventing it wholesale mid-scene.

**Step 1 — Settlement & Factions:**
- The settlement/region itself (e.g. `locations/neverwinter`, type: Settlement, with `ambientCrowd`, description, `dangerModifier`)
- **Factions** active here
- At least **one NPC per faction/district** who exists only to make the world feel lived-in — not a quest-giver, just someone with psychology, `currentActivity`, `keepAlive: true`

**Step 2 — Districts:** Every settlement needs 3-5 named districts. Each gets:
- `type: District`, `parentLocationId: <settlement>`
- `ambientCrowd` (texture of a typical moment)
- `dangerModifier` (0 = safe, 20+ = active threat)
- `description` (2-3 sensory details)

**Step 3 — Street-Level Buildings:** Every district needs 2-3 Building-type locations:
- A tavern or inn (social hub, rumor source)
- A shop, temple, or guildhall (service node, quest hook source)
- A notable landmark (theater, bathhouse, prison, barracks)
- Each gets `type: Building`, `connectedFromLocationId: <district>`, `connectionDescription`

**Step 4 — Points of Interest:** Every district AND every building should have 2-4 `pointsOfInterest` (light strings: "Fountain of the Swords", "Torn wanted poster"). For mechanically significant PoIs, add `pointOfInterestDetails` entries.

**Step 5 — Exits:** Every location must have at least one exit (auto-linked via `connectedFromLocationId` on creation). No dead ends.

**Step 6 — Plot Thread Enrichment:** Every plot thread seeded via `world_build` MUST include:
- `foreshadowingHooks` (2-4 strings) — concrete, narratable teasers the GM can deploy before the thread activates. Examples: a glimpse of someone watching from a rooftop, a rumor overheard in a tavern, a letter found in a desk, a pattern noticed across multiple scenes. These are the *before* signals.
- `clues` (2-4 entries minimum, each with `id`, `description`, `involvedEntityIds`) — discoverable evidence the party might find once the thread is active. A scrap of paper, a witness memory, a tracking mark. Each clue should be findable at a specific location or from a specific NPC.
- `resolutionCondition` — a clear, testable end condition. Not "the party talks to them" but "the party presents evidence of the Thayan camp to Maeva, and she calls off the elven war parties."
- `involvedEntityIds` — at minimum the primary NPC(s) the thread revolves around. Add faction IDs if the thread spans faction politics.

**Materializing Clues in the World:**

**If a clue references a physical object** (a letter, a journal, a ledger, a bloodstained arrow, a bounty notice, a torn map) — **seed that object as an `items[]` entry** in `world_build` with `holderId` pointing to the character or location where it can be found. Otherwise the party searches and finds nothing in the persisted world; the clue exists only as metadata, not as something they can interact with.

**Bidirectional linking:** The clue's `involvedEntityIds` must include the item ID (e.g., `"items/dunstun-journal"`). Additionally, tag the item with a plot-thread reference: `tags: ["clue:plot-threads/dunstun-confession"]`. Without this, the clue metadata and the item are orphaned from each other — `get_entity` on the item won't surface the clue's context, and the DM won't know the item's significance at a glance.

**If a clue references a witness or informant** — decide whether they need their own `chars[]` entry (recurring, named, likely to be interacted with multiple times) or can emerge from the location's `ambientCrowd` during play (transient, nameless, one-off). When unsure, keep them in the clue text and promote them via `world_build` if the party pursues them. Tag them similarly: `tags: ["witness:plot-threads/dunstun-confession"]` on their `chars[]` entry if seeding them.

**Apply this checklist BEFORE committing any `world_build` call.** Run through each layer mentally. If you catch yourself saying "I'll add that later," stop — seed it now. The cost of a missed location is a broken `get_entity` call or a dead-end scene. The cost of a missed PoI, unfilled plot thread, or non-materialized clue is flat narration without narrative scaffolding.

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

## Location Transitions & Plot Threads

After arriving at a location:
1. Call `get_entity` with the location id (partyPresent: true) to read location state + any NPCs/creatures present
2. Check `AssociatedPlotThreads` (plots referencing this location via clues or involvement)
   - For Dormant threads: weave one foreshadowing hook into scene description
   - For Active threads: surface a clue or NPC motivation hint
   - For Climax threads: immediate consequences manifest in the scene
3. Check `WorldPressure` for location-specific ENGINE WARNINGs (missing clue entities, unvisited transients, etc.)
   - Missing entity in a clue? Seed it via world_build or remove the stale reference
   - **To verify resolution:** After committing a fix via `take_turn`, pass `includeWorldState: true` and check the response's `WorldPressure` — the warning should be gone. If it's still there, your fix didn't work; investigate why.
4. Narrate arrival sensory details
5. Continue from there

**Lazy Seeding on Arrival:** If the location or its parent district/building isn't yet seeded, surface ENGINE WARNING will nudge you to create it. Use checklist above to seed it before continuing—don't let dead-end or half-described locations ruin the scene.

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

Travel and rest advance time via their own hour fields (not `minutesElapsed` — that's for other changes, set on the top-level `take_turn` request). Hunger/thirst/tiredness advance immediately:
- Short travel (2–4 hours): minor need ticks
- Long travel (8+ hours): significant need progression
- Rest recovers pools and clears tiredness instantly

## Exploration Checklist

**During play:**
- [ ] Is this a local move (same location, safe)? → `activity`
- [ ] Is this a real journey (distance, danger)? → `travel` with encounterRiskModifier
- [ ] Is this an overnight span with stakes? → `rest` (not `advance_world`)
- [ ] Did I fetch the scene (`get_entity` locations/ id, partyPresent: true) after arrival?
- [ ] Is there a check (Perception, Investigation, Survival)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll?
- [ ] Did time pass? → `minutesElapsed` on the request (rest/travel use their own hour fields instead)
- [ ] Are they in a tactical waypoint? → `poiName`/`poiDetails` to persist it
- [ ] Is the scene anchored at Settlement/Region level? → Descend to District/Building/Room first

**When seeding a new area (world_build):**
- [ ] Steps 1–5: Settlement, districts, buildings, PoIs, exits all complete?
- [ ] Every plot thread has foreshadowingHooks (2-4), clues (2-4), resolutionCondition, involvedEntityIds?
- [ ] Ready to call `world_build`?
