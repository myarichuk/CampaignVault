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

**Sub-scene detail that doesn't deserve a full Location** (a hiding spot, a stash, a lookout ledge inside an existing Building/Wilderness location) → use `location_update`'s `materializePointOfInterest` instead (see Wayfinding below), not a new Location entity. Create a full child Location when the party can return to it later, it has its own exits, or it will host its own future scenes; use a PoI for a tactical detail that only matters for the current beat.

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

Narrate the sensory outcome from the roll result—don't invent what they find.

## Encounter Resolution

Travel can trigger random encounters. Engine resolves and returns encounter NPC/creature. You narrate the scene and run the interaction (combat, negotiation, flight).

**`scene_interrupt_check`** is the sibling mechanism for crowded locations: a single-roll check (not a full travel/rest span) for whether someone steps out of the `ambientCrowd` and interrupts a tense beat. Call it after a beat that raises stakes in a crowded place — not on every line of dialogue. It has a one-interrupt-per-location-per-day cooldown, so don't call it repeatedly hoping for a hit.

```json
{
  "$type": "scene_interrupt_check",
  "locationId": "locations/high-road-leilon-stretch",
  "characterId": "chars/lyra",
  "riskModifier": 10,
  "notes": "Bloodied, wanted face, crowd already hostile"
}
```

**Every encounter/interrupt NPC has a blank sheet until you write one — don't retroactively invent motive to justify a roll result.** The engine hands you a bare `Unknown Encounter Entity`/`Figure from the Crowd` with an `ENGINE DIRECTIVE` note, not a backstory. Decide who they are (toll collector, drunk, mistaken-identity grab, actual threat) *before* resolving the check, from location flavor + the PC's visible state — not by seeing a social-roll result first and retconning a faction/plot connection into "explain" it. A successful Persuasion/Deception roll changes *how* that person does the job they already have (see `dnd-social`'s NPC Trust & Self-Interest); it doesn't let you upgrade "random road muscle" into "secretly here for the PC's backstory" after the fact.

**Clear the NPC from the scene once the encounter resolves.** These are `keepAlive: false` transients placed AT the scene location — they stay in `PresentNPCs` on every future `get_entity`/`take_turn` scene fetch at that location until their `CurrentLocationId` is explicitly cleared. The engine's own eviction sweep is day-granularity and NOT triggered by narrative resolution, so don't rely on it. As soon as you narrate the encounter as over (they leave, are dealt with, party moves on), commit, in the same batch as that narration:

```json
{
  "$type": "activity",
  "characterId": "chars/transient_encounter_cfbd70",
  "newLocationId": null,
  "updateLocation": true,
  "reason": "Encounter resolved; NPC moves on"
}
```

This doesn't delete them (they stay in the DB — reusable if genuinely still nearby), it just stops them cluttering scene presence. If you want them to matter again (recurring threat, promoted to a real NPC), use `character_update` with `keepAlive: true` or `schedule_change` instead of clearing location — don't do both.

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

Two different moves depending on how far/long/exposed the departure is — don't default to the lighter one just because it's a single field:

**Staying inside the current location** (fleeing to a corner, hiding behind the bar, ducking into an alcove, flipping a table for cover, sitting on the bar with legs dangling, sleeping on a cot) — `newActivity` alone is enough to reposition the character; it's free-text narration, carries no PoI fields, and doesn't need `newLocationId`/`updateLocation` at all when the character's location document isn't actually changing. Reserve `newLocationId`/`updateLocation` for a genuine transition to a *different*, already-existing `Location` (it must resolve to a real location — the engine rejects an invented or nonexistent id rather than silently accepting it). If the spot has a lasting physical detail worth persisting, add a *separate* `location_update` in the same commit batch — this is flavor persisted on the *existing* location, not a new place:

```json
{
  "changes": [
    {
      "$type": "activity",
      "characterId": "chars/lyra",
      "newActivity": "slipping behind the waterfall"
    },
    {
      "$type": "location_update",
      "locationId": "locations/forest",
      "materializePointOfInterest": "Hidden Stream Grotto",
      "poiDetails": "Narrow cave entrance behind waterfall, good cover from above, fresh water, no fire risk"
    }
  ]
}
```

**`poiDetails` is for durable physical facts about the PoI, never for a character's current action or state.** "Thrashed sheets and a crumpled pillow on the cot" is a physical trace worth persisting. "Lyra sleeping on the cot" or "Mira giving what help she can" is a snapshot of what someone is doing *right now* — use `newActivity` for that, plus `event`/`knowledge_update` to log the conversation/beat. Narrating a character's state through `poiDetails` goes stale the instant the beat ends, and forces every future scene refresh of the parent location to resend in full. Don't re-issue `location_update` every time the character's verb changes at the same spot — only when the room itself changes or is first established.

**One use is a place, not flavor — promote as soon as a PoI is marked occupied a second time, don't wait longer.** When a `location_update` sets `materializePointOfInterest` + `poiOccupantCharacterId` (a character is really placed there, not just described) — or it's clearly somewhere the party will return to or linger (a rented room, a hideout, a sickbed) — that already satisfies "the party can return to it later" / "will host its own future scenes" above. Stop persisting it as `poiDetails` text and promote it to a real child `Location` (same pattern as the Hidden Forest Hollow example below) *before* narrating anyone as separated from the rest of the group. The engine tracks presence per exact `locationId` only — everyone still anchored to the parent shows up as co-located with whoever you just placed at the PoI, even when they're narratively in a different room. If you skip this, the engine will flag it as an ENGINE WARNING once other NPCs are present at the parent location — treat that as a hard cue to promote, not a suggestion to defer again.

**Leaving to a real, distinct spot** (an hour into the woods, off the road to make camp, down into an unmapped ravine) — `location_update` create-and-link a real child `Location` in the same `take_turn` batch, then `travel`/`activity` into it. Don't staple this onto the parent Region/Wilderness as a PoI — a giant location used as a catch-all destination misrepresents who else is "present" there (anyone else nominally at that same broad location shows up in the scene) and never gets its own exits/danger tuned for the spot:

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
- [ ] Did I fetch the scene (`get_entity` locations/ id, partyPresent: true) after arrival?
- [ ] Is there a check (Perception, Investigation, Survival)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll?
- [ ] Did time pass? → `minutesElapsed` on the request (rest/travel use their own hour fields instead)
- [ ] Are they in a tactical waypoint (first use, transient)? → a `location_update` with `materializePointOfInterest`/`poiDetails` in the same commit, to persist a physical fact, never a character's current action/state
- [ ] Has a `location_update` marked the same PoI occupied (`poiOccupantCharacterId`) a second time, or is it clearly somewhere the party returns to/lingers? → promote it to a real child `Location` before narrating anyone as separated from the group
- [ ] Is the scene anchored at Settlement/Region level? → Descend to District/Building/Room first
- [ ] Did an encounter/`scene_interrupt_check` NPC just resolve (left, dealt with, party moved on)? → `activity` change clearing their `CurrentLocationId` in the same batch as the resolution narration

**When seeding a new area (world_build):**
- [ ] Steps 1–5: Settlement, districts, buildings, PoIs, exits all complete?
- [ ] Every plot thread has foreshadowingHooks (2-4), clues (2-4), resolutionCondition, involvedEntityIds?
- [ ] Ready to call `world_build`?
