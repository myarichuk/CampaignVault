---
name: dnd-world-change
description: Atomic take_turn batches, entity creation/updates, and world state persistence
metadata:
  type: skill
---

# World-Change Mode

You are persisting changes to the world: events, character state, items, relationships, quests, locations, and factions. All in-play mutations go through **`take_turn`** — pass `changes[]` + `narrative`, and the response echoes fresh summaries of every touched entity (no re-query needed).

## Skill Ownership (canonical homes — pointers elsewhere defer here)

`dnd-world-change`: mutation syntax, batching, required fields, delta/refresh. `dnd-bundling`: which types cohere in one beat. `dnd-combat`: turn order, action types, spell components. `dnd-exploration`: location hierarchy, travel/rest, fixtures-vs-Location, encounter cleanup. `dnd-narration`: prose craft only. `dnd-npc-interaction`: psychology, memory, initiative. `dnd-social`: checks, DCs, modifiers. `dnd-campaign-events`: pressure, quests, rumors, factions, time. `dnd-world-building`: seeding. On conflict, the owning skill wins — never restate another skill's rule inline.

## No-Op Rule (diagnosis is read-only)

Self-diagnose with queries only (`get_entity`, `search_world`, `recall_history`, `lookup`). Never call `take_turn` to inspect — every `take_turn` commits (time tick, pressure eval, refresh) even with trivial `changes[]`. `includeWorldState`/`includeParty` verify a fix already sent; they are full rebuilds, not status polls.

`narrative` is a short factual summary for the engine's event log — never the in-character text shown to the player. Tool-call efficiency governs call shape/count only; it never shortens prose.

## Atomic Turn Discipline

**Every narrative beat ends with a same-turn `take_turn` before the player responds.** The take_turn is the period at the end of every sentence.

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/npc"],
  "locationId": "locations/tavern",
  "summary": "NPC reveals secret about the mayor"
}
```

## Change Types ($type discriminators)

| Category | $type | Purpose |
|----------|-------|---------|
| Narrative | `event`, `rumor` | Record dialogue, actions, discoveries |
| Character State | `character_update`, `mood`, `knowledge_update` | Appearance, mood, memory |
| Needs/Attributes | `need`, `attribute` | Open-ended narrative drives and stats — see below |
| Relationships | `relationship`, `engagement_relation`, `spatial_position` | Numeric bonds, lasting states, tactical spacing — disambiguated below |

**`relationship` vs `engagement_relation` vs `faction_state`:** `relationship` = numeric opinion delta (-100..100, needs explicit `reason`) between two characters — a successful check never moves it by itself. `engagement_relation` = lasting physical/social state (verb + `category`; e.g. grappling/Physical, persuaded/Social) — commit explicitly except grapple/escape-grapple `ruleset_action`, which auto-applies one (engine rejects a redundant manual pair in the same batch — hard fail). `faction_state` (plural via `faction_state`, singular via `faction_reputation`) = faction-level stance/reputation, not character opinion. `spatial_position` = tactical spacing only, never a substitute for either.

**Never pair `ruleset_action` with a manual `hp`/`status`/`engagement_relation` on the same character** — damage, conditions, and grapple engagement auto-apply (engine hard-fails the batch as a duplicate). Commit those $types only for unrelated adjustments (different character, or a second commit).
**Auto-logging:** `status` and Physical/Medical `engagement_relation` self-log (no paired `event`). Everything else that matters narratively (`ruleset_action` incl. HP-only damage, Social/Attention/Proximity relations) needs its own `event`.
| NPC Behavior | `npc_initiative_nudge` | Prime a specific NPC to act/speak next based on something they just witnessed (see `dnd-npc-interaction`'s NPC Initiative section) — the engine's own scheduler can't judge a specific narrative beat the way you can. |
| Inventory | `item`, `item_update`, `item_equip`, `item_unequip`, `item_use` | Carry/drop/equip items |
| Conditions | `status`, `status_remove` | Apply/end named conditions |

**Picking up / dropping / giving an item (`$type: "item"`):** moves an *existing* item to a new holder — character, location, or container item. Never narrate a pickup without it, or the item stays owned by its old holder and `get_entity` on the location will still list it as `visibleItems`.

```json
{ "$type": "item", "itemId": "items/gold-coin", "toHolderId": "chars/lyra" }
```

For a brand-new item (loot that didn't exist yet), create it via `world_build`'s `items[]` first, then transfer if needed. If it matches a known template, set `definitionName` (check via `lookup` kind:`items`) instead of typing out category/tags/properties/equip fields by hand — any of those you also set explicitly on the same entry still override the template.
| Combat/Mechanics | `ruleset_action`, `status`, `hp`, `resource` | Dice rolls, HP, spell slots |

**Persistent physical state (gear worn, conditions, lasting appearance changes) needs a commit, or it silently reverts next scene** — same failure mode as the item-pickup warning above. Putting on a gifted item → `item_equip`; cutting someone's bonds, ending a condition → `status_remove` (not just narration — "still bound" a few beats later is this bug); a scar or new outfit that should stick → `character_update`'s appearance fields; gear destroyed/dissolved/lost → `item_unequip`/`item_update`/`archive_entity`.

Set `event.impliesPersistentPhysicalChange: true` on the paired `event` whenever a beat does this — the server checks it against what the batch actually committed and fires a `narrativeReminder` if they don't match, catching the case where you narrated the change but forgot the commit. This is a self-report, not a text scan: it only works if you actually set the flag, but unlike guessing from prose it never misfires on ordinary narration.
| Activities | `activity`, `travel`, `rest` | Movement, waiting, recovery |
| Quests/World | `quest_progress`, `rumor`, `faction_state`, `plot_thread_progress` | Story progression |
| Locations | `location_update` | State, tags/features, danger modifier |

**`location_update.description` is static prose** — independent of `currentState` and never auto-rewritten. If a state change would make the old description contradict canon (e.g. a body removed from a scene, a fire put out), explicitly resend a new `description` in the same `location_update`, or `get_entity` will keep surfacing the stale text.
| Campaign | `campaign_update` | Narrative focus tags (full replacement list) |

Call `lookup kind=commit_schema` for the machine-readable field list per $type.

## Needs, Attributes & Traits — the open-ended bags

Three separate extension points exist for narrative state that isn't one of the named fields above. They are NOT interchangeable — picking the wrong one silently drops the value from the surface that should show it.

- **`need`** (`$type: "need"`) — pushes `characterId`/`need`/`delta` into that character's `NeedsProfile.ActiveNeeds`. The name is unrestricted; invent any narrative-appropriate need (`paranoia`, `bloodlust`, `homesickness`, `stress`, `fatigue`). Only the core four (`hunger`, `thirst`, `tiredness`, `social_drive`) auto-tick from `minutesElapsed`/`advance_world` — an invented need moves **only** when you explicitly commit a `need` change for it, so if a need should track ongoing time pressure (not just discrete events), either give it an `accumulationRate` once (below) or push it yourself every turn it's relevant. A prose-only tracked bar dies at the session boundary/compaction; a committed `need` survives. Needs surface to you via `take_turn`'s `KnownNeeds` and participate in delta-mode "significant mover" filtering — this is the bag to use for anything you want the player-facing need list to show.
- **`attribute`** (`$type: "attribute"`) — pushes `characterId`/`attribute`/`value`(+`isDelta`) into `SystemStats.Attributes`, a float bag clamped 0–100 (e.g. `corruption`, `reputation`, `fear`, `debt_pressure`). Never surfaces in `KnownNeeds` and is not part of the needs-accumulation sweep — use this for a persistent narrative score you'll reference in prose or pressure checks, not for anything meant to read like a need.
- **`character_update.systemStats.traits`** — a `Dictionary<string,string>` inside `character_update`'s `systemStats` object (`SystemStats.Traits`), for open-ended string facts that don't fit a float (categorical tags, freeform descriptors). Example:
  ```json
  { "$type": "character_update", "characterId": "chars/lyra", "systemStats": { "$system": "dnd5e", "traits": { "disguise_persona": "traveling merchant" } } }
  ```
  Sending one key patches only that key — it merges into the existing `Traits` dict rather than replacing it, so you don't need to resend every trait the character already has.

Rule of thumb: something the player should see tracked as a rising/falling need → `need`. A persistent narrative score/flag → `attribute`. A freeform string fact → `character_update.systemStats.traits`.

**Giving a custom need its own passive drift:** set `accumulationRate` (points/day) on a `need` commit once — omit it to leave the current rate unchanged, set `0` to stop drift without deleting history. It is independent of `delta`, so you can set both in one commit to establish a starting value and its ongoing rate together; if you're only setting the rate, pass `delta: 0` so you don't also push the value.

`accumulationRate` is for steady time-based drift only. Event-driven spikes — a big drink, a magical effect — still use a direct `delta` push in the *same* commit as the triggering event. Don't model "that ale hit harder" as a rate change.

```json
[
  { "$type": "need", "characterId": "chars/grog", "need": "bladder", "delta": 0, "accumulationRate": 20 },
  { "$type": "event", "involved": ["chars/grog"], "category": "Conversation", "summary": "Grog downs a full tankard of ale" },
  { "$type": "need", "characterId": "chars/grog", "need": "bladder", "delta": 15 }
]
```

The first `need` establishes `bladder` drifting at 20/day with no immediate push; the second is the same-turn spike for that specific drink.

## Entity Creation

Never create entities through take_turn changes — there are no `_create` $types. Use `world_build` (batch: characters, locations, items, factions, quests, rumors, plotThreads, creatures, spells, feats, lore, needDescriptors), even for a single new entity (a one-item batch is fine). It reports a merge (not a duplicate) if the id already exists.

**Seed before you name.** Never narrate a named actor into existence: before giving someone a name, a voice, or an action distinct from the crowd, check the scene roster (`presentNPCs[].id`, which always lists everyone present) and `cards[]`/`npcs[]`. Missing? `world_build` them (a `chars[]` entry, `keepAlive: true` if worth keeping) *before or in the same batch as* the narration — never after. Unnamed background stays unnamed (`ambientCrowd` flavor needs no ID). A `world_build`'d name you then reference that still fails means the ID doesn't exist server-side — `search_world` before retrying, don't re-narrate.

**Before calling world_build**, run the world-building seeding checklist in `dnd-world-building` — especially the 6-step location depth + plot thread enrichment check, and its item-template/tag lookup step. A missed district, missing fixtures, or unfilled clues are gaps that surface as a broken `get_entity` or a flat narration later.

**Plot thread clues must materialize as real items or NPCs:** If a clue references a physical object, seed it as an `items[]` entry. The clue's `involvedEntityIds` must include the item ID so `get_entity` on the item surfaces clue context. Tag the item: `tags: ["clue:plot-threads/..."]`. Without this, the party searches the world and finds nothing.

New locations follow the same rule — see `dnd-exploration` for the Region→Settlement→District→Building→Room hierarchy and when a spot needs a full Location vs. just narration.

## Batch Changes (one take_turn, multiple changes)

Atomic all-or-nothing; if any change fails, the entire batch rolls back — **nothing is saved, including changes earlier in the same batch that individually "succeeded."** Fix the failing entry and resend the FULL corrected batch, not just the fix.

```json
[
  { "$type": "event", "involved": ["chars/pc", "chars/npc"], "category": "Conversation", "summary": "..." },
  { "$type": "relationship", "characterId": "chars/npc", "targetId": "chars/pc", "delta": 15 },
  { "$type": "knowledge_update", "characterId": "chars/npc", "topic": "PC_goal", "details": "..." }
]
```

## Required Fields (never rely on defaults)

- `ruleset_action.actionType` — "Attack", "Spell", "SkillCheck", etc.
- `quest_progress.newState` — `Open`, `InProgress`, `Complete`, `Failed`, `Skipped`
- `quest_progress` — must also include `objectiveIndex` or `objectiveName`; there's no default, and omitting both hard-fails the change (and the whole batch with it).
- `rest.intendedHours` — always set explicitly (positive number)
- `event.locationId` — never put location ID inside `involved`
- `engagement_relation.category` — always set (Physical/Medical/Social/Attention/Proximity); omitted unrecognized verbs default to Social/Soft (no travel gate — verified in `EngagementRelationCatalog.InferCategory`; Physical is only for catalog-marked blocking verbs)
- `knowledge_update.sourceEventIds` — required when `source` is `Witnessed` or `Experienced` (the character was directly there). Pass a client-chosen `eventId` on the paired `event`/`ruleset_action` change in the *same* batch and reference it here — the engine won't hand back a mid-batch ID for reuse, so you must pre-choose one. `Heard`/`Told` (secondhand/rumor) don't need this.

## Don't Let a Roll's Outcome Evaporate

You can't write a `knowledge_update` worded around a roll's outcome in the *same* batch as the `ruleset_action` that produces it — you don't know pass/fail until the engine responds. That's correct and unavoidable. But if the check has **no independent mechanical side effect** (a pure Perception/Insight/social read — no HP change, no item transfer, no quest progress attached), nothing else persists the result: the engine's computed roll narrative only reaches you in that turn's response text, and the auto-logged `Event` for the beat is built from your *pre-roll* narrative, not the outcome. If you don't write it down, it's gone by next session (or after a compaction) — not narrated wrong, just unrecoverable. Fold the resolved outcome into a `knowledge_update`/`event` in your very next batch (the one you're sending anyway, not a dedicated extra call) whenever the result is something the character would remember. A roll that *does* change HP/items/quest state doesn't need this — that state change already persists on its own.

## Time Tracking

Set `minutesElapsed` on the top-level `take_turn` request (sibling to `changes`/`narrative`), not inside individual changes:
- Banter: 2–5 minutes
- Tense interrogation: 60–180 minutes
- Long rest: 480 minutes (8 hours)

Time accumulates and immediately nudges hunger/thirst/tiredness. Rest/travel changes advance time via their own hour fields instead — don't also set `minutesElapsed` alongside them.

**Calendar date:** `WorldStateView.Time.FormattedDate` (e.g. "Day 12, Month 3, Year 1492 (Current Era) — Morning") is a ready-to-narrate sentence — use it directly rather than assembling one from the raw `Year`/`Month`/`Day`/`Epoch` fields. Reference it when a scene calls for grounding the party in time (a new day, a festival, "how long have we been here"), not on every beat.

## Narrow vs. Broad Mutations

- **Broad scope** (structural changes: field rewrites, entity creation) → `world_build`
- **Narrow scope** (tags, state, position, activity, HP, resource tweaks) → `take_turn` with `character_update`, `item_update`, `status`, `resource`, etc.
- **Ambiguous** (item's equipZones, character's Psychology/Needs profile) → `world_build` (single-item batch)

Example: Don't use take_turn changes to rewrite a character's entire Psychology. Use `character_update` for narrow tags/mood, or `world_build` if you're restructuring Psychology deeply.

**Delta-mode nulls mean "unchanged," not "gone."** On a `mode: delta` turn, auto-refreshed scenes/NPCs omit appearance, gear, local rumors, Psychology, and Memory that didn't change (cards come once per session) — expect to already have them from the last full reseed. Only trust an omission as "gone" after a fresh `get_entity`. When memory/psychology may be stale (gap, resume), add `memoriesOnlyCharacterId` (memory only — cheapest) or `fullDetailCharacterId` to the next `take_turn` instead of assuming.

`take_turn` echoes touched-entity summaries automatically (cap 6 NPCs / 3 scenes); extend with opt-ins on the SAME call instead of a follow-up query: `includeParty: true` (only when a PC's HP/slots/gold/needs/AC/gear changed or before first narrating PC need values this session — `partyFingerprint` already covers HP + location, so conversation beats don't need it), `includeWorldState: true` (full world-state rebuild every time — reserve for pressure/verification, never a default), `fullDetailCharacterId`/`fullDetailLocationId` (one deep dossier), `extraCharacterIds`/`extraLocationIds` (untouched entities). Standalone reads with no mutation use `get_entity`. If an expected section comes back null, check the response's `warnings` array.

Don't set `forceFullReseed: true` unless context was just compacted or a fresh session started — the engine already decides `mode: full` vs `delta` each turn, and a same-location activity update stays delta-eligible on its own.

## Checklist (per-beat — scene/session tiers live in `dnd-narration` / `dnd-campaign-events`)

- [ ] Did I narrate a beat? → `take_turn` before player responds
- [ ] Is this dialogue? → `event` with Conversation category + all speakers in `involved`
- [ ] Did something change (mood, position, item)? → Include in the batch
- [ ] Is time passing (banter, rest, travel)? → `minutesElapsed` on the top-level request (rest/travel use their own hour fields instead)
- [ ] Did a character level up or cast a spell? → Include `ruleset_action` or `resource` spend
- [ ] Are multiple changes happening at once? → Batch them in one `take_turn` changes array
- [ ] Did I send all required fields? → Check actionType, newState, intendedHours, locationId
