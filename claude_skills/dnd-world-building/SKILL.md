---
name: dnd-world-building
description: Seeding with world_build — seeding order, the depth a new area needs, speaking NPCs, secrets and traps, plot-thread scaffolding, clues as real items, item templates
metadata:
  type: skill
---

# World Building

Seeding with `world_build`: session 0, a new settlement or region, a new plot thread mid-campaign, or a single person or place the story is about to name. It is one atomic batch (at most 100 entries); an existing id is merged. This skill is the process; `lookup kind=help topic=world-building` has the field-level schema, the per-ruleset `systemStats`, and a full example.

## Order

`world_build` processes its arrays in a fixed order whatever order you send them in, so references forward within one batch are safe (a quest's `giverId` naming a character seeded in the same call):

1. **locations**: the starting hub first, then what it links to.
2. **factions** active in the region.
3. **creatures, spells, feats**: only homebrew.
4. **characters**: PCs (`isPc: true`), then the named NPCs the opening actually needs. Combat-capable NPCs need `systemStats`; gear is a separate `items[]` entry held by them.
5. **items**: starting gear with `holderId` set.
6. **quests**: the opening hook, if ready.
7. **plotThreads**: scaffolding for arcs seeded ahead.
8. **lore** worth being searchable.
9. **rumors**, sparingly; most should come out of play.
10. **needDescriptors** explaining any custom needs.

## A new area, layer by layer

The party should be able to move through the world at this resolution without you inventing it mid-scene. The location hierarchy itself is in `dnd-exploration`.

1. **Settlement and factions.** The settlement (type Settlement, `ambientCrowd`, description, `dangerModifier`), the factions active there, and at least one NPC per faction or district who exists only to make the place lived-in: psychology, `currentActivity`, `keepAlive: true`.
2. **Districts.** Three to five per settlement, each with `parentLocationId`, `ambientCrowd` (a typical moment), `dangerModifier` (0 safe, 20+ active threat) and two or three sensory details in the description.
3. **Buildings.** Two or three per district: somewhere social (a tavern or inn, where rumors live), a service (a shop, temple or guildhall, where hooks come from), and a landmark (a theatre, a bathhouse, a prison), each linked with `connectedFromLocationId` and `connectionDescription`.
4. **Fixtures and secrets.** A description that names what is there is enough for atmosphere. Seed an entity only when it matters: a place to enter is a child location with an exit, a fixture worth touching (a desk, a notice board, an altar) is an item held by the location, and its contents are items held by it. For a place tied to a plot thread, quest or secret, ask what is hidden there and who hid it, and seed zero to two answers, each with a reason, never as a quota. The engine keeps them off the scene and resolves finding them:
   - a secret passage: an exit with `hidden: true`, `discoverDc` and `intent` ("the smuggler's way down; a draft moves the candle");
   - a concealed object: an item with `hidden: true` and `discoverDc`, held by the location or by a fixture (a key in the desk);
   - a secret on a fixture: an `ItemDetail` with `hidden: true`, `discoverDc` and `intent` (a false bottom, a glyph under the varnish);
   - a trap: a `hazard` on an exit (fires on passing), an item (fires when taken) or the location's `hazards` (fires on entering): `{name, trigger, detectDc, disarmDc, effect, saveDc, saveAbility, intent}`.
5. **Exits.** Every location has at least one; `connectedFromLocationId` creates it. No dead ends.
6. **Plot threads** (below).

## Speaking NPCs

Anyone who will talk needs enough to have a voice: `psychology.traits`, a want and a fear, and at least two memories that give them opinions. Without these every NPC sounds the same.

```json
{ "characters": [ { "id": "chars/hedda-miller", "name": "Hedda", "currentLocationId": "locations/old-mill", "currentActivity": "Sharpening a sickle on the step", "keepAlive": true,
  "psychology": { "traits": ["blunt", "suspicious of townsfolk"], "wants": ["the mill kept in the family"], "fears": ["the bailiff's men"],
    "memories": {
      "the-flood": { "topic": "the-flood", "details": "Lost her husband when the weir broke two springs ago; blames the lord's steward for the repairs he never paid for" },
      "strangers": { "topic": "strangers", "details": "The last travellers who asked about the mill were the steward's surveyors" } } } } ] }
```

## Plot threads

Every plot thread has:
- `foreshadowingHooks`: two to four teasers you can narrate;
- `clues`: two to four, each with `id`, `description` and `involvedEntityIds` (physical, behavioural or relational);
- a testable `resolutionCondition` ("the party shows Maeva proof of the Thayan camp and she calls off the war parties", not "the party talks to them");
- `involvedEntityIds`: the main NPCs and factions.

**Clues must exist in the world.** A clue that names an object (a letter, a ledger, a bloodied arrow, a torn map) needs that object as an `items[]` entry with `holderId` where it can be found, the item's id in the clue's `involvedEntityIds`, and a tag back to the thread (`tags: ["clue:plot-threads/dunstun-confession"]`). Otherwise the party searches and finds nothing. A witness who will recur gets a `characters[]` entry tagged the same way (`witness:plot-threads/...`); a one-off can stay in the clue text and emerge from the crowd, to be seeded if the party pursues them.

## Items

`coreCategory`, equip zones and layers are open strings, so outfits, tools, consumables and artifacts are all items, and a plugin may add its own categories and zones. Before typing an item's fields by hand:

1. `lookup kind=items` (filter by `query`, `category` or `tag`). If a template fits (SRD, homebrew or a plugin pack), set `definitionName`; fields you also set still override it.
2. For a homebrew item's tags, check `lookup kind=item_tags` first and reuse an existing tag rather than a near-duplicate, or tag filters stop matching it.

## Checklist

- [ ] Settlement, districts, buildings, fixtures and secrets where they matter, and exits.
- [ ] Every NPC who will speak has traits, a want, a fear and two memories.
- [ ] Every plot thread has hooks, clues, a testable resolution and its involved entities.
- [ ] Every clue that names an object has its item, linked both ways.
- [ ] Items checked against templates and existing tags.
