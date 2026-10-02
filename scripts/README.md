# Content generator scripts

These regenerate SRD/ORC-licensed `RulesetData/*.yaml` content from the two
official source APIs. They are one-shot fetch-and-overwrite tools, not
enrichment passes: each run pulls the full current dataset from the network
and rewrites the YAML it's responsible for from scratch. Re-running one is
always safe (idempotent — same source data in, same files out) and requires
no local state beyond the repo itself.

Requires network access to both hosts below. Nothing in `CampaignVault.csproj`
depends on running these at build time — they're a content-authoring step you
run manually, then commit the resulting YAML like any other source file.

## Sources

- **`www.dnd5eapi.co`** — dnd5e content. A community-run wrapper over the
  System Reference Document 5.1 by Wizards of the Coast LLC, CC BY 4.0. See
  `LICENSING.md`.
- **`elasticsearch.aonprd.com`** — pf2e content. The official Paizo-run
  Archives of Nethys search endpoint, queried directly (no wrapper). Every
  query in every script below is scoped to
  `primary_source.keyword: ["Player Core", "Player Core 2"]` — the two 2023
  Remaster core rulebooks released under the ORC License. **Never widen that
  filter** to other AoN sourcebooks (Lost Omens, adventure paths, legacy
  pre-Remaster books) without re-checking `LICENSING.md`'s scope rationale;
  dropping it silently pulls in non-ORC content (confirmed live: an unscoped
  `category:spell` query for "Fireball" returns the pre-Remaster Core
  Rulebook version, not the Player Core one — it carries a `remaster_id`
  field pointing at the actual remastered document instead).

## `generate_spells.py`

```
python3 scripts/generate_spells.py
```

Regenerates **every** file in `RulesetData/{dnd5e,pf2e}/spells/` — the
directory is fully wiped and rebuilt each run, so nothing there is
hand-authored. dnd5e spells include structured damage/save/heal/AoE fields
(`dnd5eapi.co`'s spell detail JSON has them); pf2e spells don't — AoN's pf2e
spell documents carry damage only as prose text, not a structured field, so
pf2e spell damage validation is out of scope engine-side (see
`Dnd5eRulesetResolver` vs. the lack of an equivalent pf2e validator).

Can run either half only, e.g. from a Python shell: `generate_dnd5e()` or
`generate_pf2e()` — useful if only one source API is reachable.

### `spell_damage_overlay.yaml` (hand-authored, never regenerated)

The one exception to "nothing there is hand-authored": `generate_dnd5e()`
merges this file's entries into the generated dnd5e spell YAMLs verbatim
after parsing the API data. It carries **shape metadata** for the ~half-dozen
SRD spells whose rules don't fit the engine's single-roll model and that no
API field describes — multi-instance counts (`instanceCountAtSlotLevel` +
`perInstanceDamageAtSlotLevel`, e.g. Magic Missile's darts), auto-hit
(`requiresAttackRoll: false`), HP-affect pools (`damageIsPool`, Sleep),
miss splash (`onMiss: half`) + delayed ticks (`delayedTick`, Acid Arrow), and
multi-pool damage (`damagePools` + `upcastChoice`, Flame Strike).

Overlay fields only ever ADD keys; API-derived dice are untouched (a
collision fails the regen loudly). The one partial exception is Flame
Strike's `damagePools`: its API upcast entries are unparseable ("4d6 OR 5d6")
so the generator's dice gate skips API pools and the overlay supplies
corrected base pools — still additive, since the flat table keeps the raw
API strings for reference. Ice Storm's and Meteor Swarm's pools need no
overlay entry at all: both pools come straight from the API.

Evidentiary standard: every entry cites the exact SRD rules text backing it —
no entry without a quote. After any regen, diff-review: every file except the
overlay-flagged spells (plus any new `damagePools` the API grew) must be
byte-identical to before.

### `spell_summon_overlay.yaml` (hand-authored, never regenerated)

Same pattern as the damage overlay, but for **summoning**: `generate_dnd5e()`
merges each entry as a `summon:` block (see `SpellDefinition.Summon`).
`creatures` names handbook refs that must exist under
`RulesetData/dnd5e/creatures/` or the regen fails; `inlineSeed` covers stock
summons with no catalog entry. `instant_summons` is deliberately excluded —
it retrieves an object, not a creature. The pf2e twin,
`spell_summon_overlay_pf2e.yaml`, works the same way for `generate_pf2e()`
(table levels are spell ranks 1–10, refs resolve under `pf2e/creatures/`).

## `generate_pf2e_feats.py`

```
python3 scripts/generate_pf2e_feats.py
```

Regenerates `RulesetData/pf2e/feats/` (also fully wiped and rebuilt). Reads
each feat's structured `prerequisite` field from AoN rather than parsing free
text — an earlier regex-based approach over prose prerequisites proved
fragile and was removed.

Each feat also gets a `category` from its traits (the character builder's feat
steps filter on it): `skill` (the Skill trait; such feats are General too),
`general`, `class` (a class trait), `ancestry` (AoN's Ancestry trait group) or
`archetype`. A feat matching none stops the run. Ancestry feats list their
`ancestries` (the traits that name an ancestry or versatile heritage), skill
feats their `skills` (AoN's `skill` field).

The prerequisite text is kept as written, and the parts the character builder
can check become `prerequisites:` entries: a skill rank ("Trained in
Athletics"), an attribute modifier ("Strength +2"), another feat by name
("Shield Block", "Dueling Parry (Fighter)"), a class feature option ("leaf
order", "warrior muse", "thief racket", matched against the Player Core
options `generate_pf2e_classes.py` writes), and "or" lists of those. Parts it
can't read (a Lore, a Perception rank, prose) are left to the text; 799 of the
981 feats with a prerequisite get at least one checkable entry.

## `generate_pf2e_origins.py`

```
python3 scripts/generate_pf2e_origins.py
```

Regenerates `RulesetData/pf2e/ancestries/` and `backgrounds/` (both fully
wiped and rebuilt), common rarity only, like the feats. Currently 8 ancestries
(dwarf, elf, gnome, goblin, halfling, human, leshy, orc; the Player Core 2
ancestries are uncommon), 44 heritages and 56 backgrounds.

- Ancestries: `hp`, `size`, `baseSpeed`, the fixed boosts and flaw as
  `abilityBonuses` (+1 / -1, which the PF2e bootstrap applies as modifier
  deltas), `freeBoosts`, the ancestry's own features as `traits` (the
  level-3 titles on its page, minus the stat lines), and `heritages[]`. AoN
  heritages carry no ancestry field: a heritage belongs to the ancestry its
  name ends with ("Rock Dwarf"). Versatile heritages (Aiuvarin, Dromaar, ...)
  match none and aren't listed yet; the run prints them.
- Backgrounds: `boosts` (one of the two background boosts must go to one of
  these; empty means both are free), `skillProficiencies`, or `skillOptions`
  when AoN lists two skills (Hermit: Nature or Occultism, a choice), `lore`
  (the Lore skill, or the text describing the choice), and `skillFeat`.
- Descriptions are the first paragraph of the page, not AoN's `summary`, which
  is cut off with "…".

## `generate_pf2e_classes.py`

```
python3 scripts/generate_pf2e_classes.py
```

For each class already in `RulesetData/pf2e/classes/` (the Player Core eight),
rebuilds `progressions/<class>.yaml` from the class page's two tables: the
class features per level (class, skill, general and ancestry feats and
attribute boosts each level brings) and the spells per day. `cantripsKnown` /
`spellsKnown` are what the builder has a caster pick: a wizard's spellbook and
a witch's familiar hold 10 cantrips and five 1st-rank spells, two more spells a
level; a bard's repertoire and a cleric's or druid's prepared spells are the
spells-per-day table.

`classes/<class>.yaml` is a mixed file: the fields above the
`# Generated by ...` line (casterType, spellcastingAbility, pools, aliases) are
hand-kept and left alone; `hitDie` (the class's HP per level) and
`skillChoices` (`trained` skills, a `oneOf` pick such as the fighter's
Acrobatics or Athletics, and `count` more of any skill) are rewritten. A skill
"determined by your order / racket / patron" comes from that class feature's
option; one determined by a deity is still a free pick (deities are setting
content, so there is no deity choice). The rogue's key attribute is Dexterity;
its rackets carry the others.

Progressions also carry the choices the builder and `level_up` ask for:

- Level 1 class features (`CLASS_CHOICES`): muse, doctrine, druidic order,
  hunter's edge, racket, patron, arcane school and arcane thesis, each option an
  AoN entry of that category (Player Core, common) with its first sentence that
  names no setting place or god (a name in `SETTING_NAMES` skips the sentence),
  the `skills` it trains ("You're trained in Thievery", "Patron Skill
  Religion"), `extraSkills` (the mastermind's "one of the following") and
  `keyAbility` ("You can choose Strength as your key attribute"). The druidic
  orders' skills aren't in their AoN entries and are kept in `ORDER_SKILLS`; an
  order missing there stops the run.
- Each level's "skill increase" (`SkillIncrease`) and, above level 1, its
  "attribute boosts" (`AttributeBoosts`, four).

## `generate_items.py`

```
python3 scripts/generate_items.py
```

Regenerates dnd5e weapon/armor items into `RulesetData/dnd5e/items/` from
`dnd5eapi.co`'s `equipment-categories/{weapon,armor}` endpoints (mundane
items only — both endpoints also list magic items such as *Vorpal Sword* or
*Armor +1*, which carry no clean fixed stat block and are filtered out by
checking the entry's `url`, not by name-matching). Currently produces 37
weapons + 13 armor/shields.

Unlike the two generators above, **this one does not wipe the directory
first** — `RulesetData/dnd5e/items/` is a mixed hand-authored/generated
directory (`climbers_kit.yaml` is a Tool, hand-authored, and is never touched
since Tool isn't a weapon/armor category SRD exposes). The script only
writes the specific slugs it generates, so anything outside dnd5e
weapons/armor is left alone automatically. `longsword.yaml` *is* one of the
slugs SRD returns, so it gets overwritten on every run — this replaces its
hand-written flavor description with the same generated factual one every
other item gets, and adds equip metadata (`equipZones`/`equipLayer`/
`twoHanded`) the hand-authored version never had (without it, that item
can't actually be equipped via `item_equip`).

## `generate_pf2e_items.py`

```
python3 scripts/generate_pf2e_items.py
```

Regenerates pf2e weapon/armor/shield items into `RulesetData/pf2e/items/` from
three separate AoN document categories: `category: "weapon"`, `category:
"armor"`, **and** `category: "shield"` — pf2e shields are their own AoN
category, not an `armor_category` subtype like dnd5e's `armor_category:
"Shield"`. Found by noticing the 4 real base shields (Buckler/Wooden/Steel/
Tower, each with their own `ac`/`hardness`/`hp`) are absent from both
`category: "armor"` (13 hits, all body armor) and `category: "equipment"`'s
Shields subcategory (precious-material variants only, no base stats of
their own). Scoped to `rarity: "common"`, same as `generate_pf2e_feats.py` — this drops
ancestry-specific Uncommon weapons (Dwarven Waraxe, Gnome Hooked Hammer, ...)
without needing separate ancestry-aware filtering.

This directory didn't exist before this script and has no hand-authored
content, so — unlike `generate_items.py`'s dnd5e `items/` directory — it's
fully wiped and rebuilt every run, same as `generate_spells.py`/
`generate_pf2e_feats.py`. Currently produces 53 weapons + 12 armor + 4
shields (69 total).

pf2e's own AC math turns out simpler than dnd5e's: an item's `ac` field is
already the flat AC-bonus contribution (no "10 + dex" total-AC shape to
subtract 10 from, unlike dnd5e body armor in `generate_items.py`), and a
`dex_cap` field maps straight onto `Properties["dexCap"]` (the numeric
"PF2e style" convention `ArmorParameterResolver.cs` already special-cases,
ahead of the dnd5e-only `armorType` fallback). pf2e also turns out to *have*
a versatile-damage mechanic after all (the `Two-Hand` trait, e.g. Bastard
Sword `1d8`/`1d12`) — captured the same way as dnd5e's Versatile
(`damageVersatile`), correcting this repo's earlier assumption that pf2e had
no such concept.

Known gap: Shield Bash/Boss/Spikes (pf2e's shield-as-weapon options) are
generated as ordinary `MainHand` weapons since this engine has no "attached
to another equipped item" concept — harmless for attack resolution, but
`item_equip`-ing one while a real weapon is already in `MainHand` will
report a conflict that doesn't match pf2e's actual "costs no hand" rule.

# Other scripts

## `pack-grok.sh` (Grok Web kit)

```
scripts/pack-grok.sh [--campaign <slug>] [--pcs "chars/id — Name, ..."] [--ruleset dnd5e|pf2e|narrative] [--copy] [--dry-run]
```

Builds `dist/grok/` (gitignored): the Grok project instructions (`project-prompt.txt`, from the
recommended system prompt plus `grok/project-prompt.md`), the knowledge files with stable names
(`00-style-anchor.md`, `skill-<name>.md` from `claude_skills/` or a `grok/skills/` variant), a
`MANIFEST.json` of hashes, `changed/` with only the files that differ from the previous pack, and
`grok-kit.zip` with the full set. Logic lives in `pack_grok.py` (plain Python 3, no packages). See
the "Grok Web" section of `INSTALLATION.md` for the upload flow. Replaces the old root
`pack-skills-for-grok.sh`.

## `measure/` (narration measurement)

`measure/prose_stats.py` reads a transcript exported from the Unity client (F12 Inspector →
EXPORT TRANSCRIPT) and reports prose statistics per turn, or compares two runs.
`measure/narration-beats.md` is the fixed 8-beat script to play for those runs.
