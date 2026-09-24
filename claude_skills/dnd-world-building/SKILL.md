---
name: dnd-world-building
description: Seeding a new campaign, settlement, or region via world_build — location depth, plot thread scaffolding, clue materialization, and item templates
metadata:
  type: skill
---

# World-Building Mode

You are seeding new content via `world_build` — session 0, arrival in a new settlement, entering a new region, or introducing a new plot thread mid-campaign.

## Recommended Seeding Order

`world_build` dispatches in a fixed order regardless of array order in your call, so forward references within the same batch are safe (e.g. a quest's `giverId` pointing at a character seeded later in the same call):

1. **locations** — the starting hub/region first, then anywhere it links to.
2. **factions** — any powers already active in the region.
3. **creatures / spells / feats** — only if this campaign has homebrew content.
4. **characters** — PCs first (`isPc: true`), then the named NPCs the opening scene actually needs.
5. **items** — starting gear, set `holderId` to the owning character. See **Items** below.
6. **quests** — the opening hook, if you have one ready.
7. **plotThreads** — DM-only scaffolding for arcs you're seeding in advance. See **Plot Thread Enrichment** below.
8. **lore** — background/history entries worth being searchable.
9. **rumors** — seed sparingly; most should emerge from play, not a pre-written list.
10. **needDescriptors** — human-readable explanations for any custom needs your NPCs track.

For the exact field-level schema and a full copy-paste JSON example, call `get_help topic=world-building` — this skill covers the *process*, that tool call covers the *syntax*.

## World-Building Seeding Checklist (Mandatory Rigor)

When seeding a new area, apply these layers in order. **Any missing layer is a gap** — the party should navigate the world at this resolution without the GM inventing it wholesale mid-scene. (Location hierarchy — Region → Settlement → District → Building → Room — and when a spot needs a full `Location` vs. just a PoI is covered in `dnd-exploration`; this checklist assumes that model.)

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

**Step 6 — Plot Thread Enrichment:** Every plot thread seeded via `world_build` MUST include `foreshadowingHooks` (2-4 narratable teasers), `clues` (2-4 entries, each with `id`, `description`, `involvedEntityIds` — clue types: physical, behavioral, relational), a testable `resolutionCondition` ("party presents evidence of the Thayan camp to Maeva, and she calls off the elven war parties" — not "the party talks to them"), and `involvedEntityIds` (primary NPCs + factions). (Canonical counts: 2–4 everywhere; `dnd-campaign-events` and `dnd-npc-interaction` defer here.)

**Apply this checklist BEFORE committing any `world_build` call.** Run through each layer mentally. If you catch yourself saying "I'll add that later," stop — seed it now. The cost of a missed location is a broken `get_entity` call or a dead-end scene. The cost of a missed PoI, unfilled plot thread, or non-materialized clue is flat narration without narrative scaffolding.

## Materializing Clues in the World

**If a clue references a physical object** (a letter, a journal, a ledger, a bloodstained arrow, a bounty notice, a torn map) — **seed that object as an `items[]` entry** in `world_build` with `holderId` pointing to the character or location where it can be found. Otherwise the party searches and finds nothing in the persisted world; the clue exists only as metadata, not as something they can interact with.

**Bidirectional linking:** The clue's `involvedEntityIds` must include the item ID (e.g., `"items/dunstun-journal"`). Additionally, tag the item with a plot-thread reference: `tags: ["clue:plot-threads/dunstun-confession"]`. Without this, the clue metadata and the item are orphaned from each other — `get_entity` on the item won't surface the clue's context, and the DM won't know the item's significance at a glance.

**If a clue references a witness or informant** — decide whether they need their own `chars[]` entry (recurring, named, likely to be interacted with multiple times) or can emerge from the location's `ambientCrowd` during play (transient, nameless, one-off). When unsure, keep them in the clue text and promote them via `world_build` if the party pursues them. Tag them similarly: `tags: ["witness:plot-threads/dunstun-confession"]` on their `chars[]` entry if seeding them.

## Items

Items aren't restricted to weapons/armor — `coreCategory` plus the open `properties`/`tags` bag cover outfits, tools, consumables, and artifacts uniformly, and `coreCategory` and equip `equipZones`/`equipLayer` are open strings, not a fixed list — a plugin pack may define its own (e.g. `category: Jewelry`, zones like `septum`/`anklet`).

**Before hand-typing an item's fields, check whether it's already a known template:**
1. `get_rules_reference` kind:'items' (filter by `itemNameQuery`/`itemCategory`/`itemTag`) — if a matching template exists (SRD or a homebrew/plugin pack), set `definitionName` on the `items[]` entry instead of typing out `coreCategory`/`tags`/`properties`/equip fields by hand. Anything you *also* set explicitly on that same entry still overrides the template.
2. If no template fits and you're inventing tags for a homebrew item, check `get_rules_reference` kind:'item_tags' first — reuse an existing tag (e.g. `exotic`) instead of a near-duplicate (`rare`), or the `itemTag` filter above silently stops matching it.

## Checklist

**When seeding a new area (world_build):**
- [ ] Steps 1–5: Settlement, districts, buildings, PoIs, exits all complete?
- [ ] Every plot thread has foreshadowingHooks (2-4), clues (2-4), resolutionCondition, involvedEntityIds?
- [ ] Every clue referencing a physical object has a matching, bidirectionally-tagged `items[]` entry?
- [ ] For each item: checked kind:'items' for a `definitionName` match, or kind:'item_tags' before inventing a new tag?
- [ ] Ready to call `world_build`?
