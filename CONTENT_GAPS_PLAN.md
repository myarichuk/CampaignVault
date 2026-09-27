# Content Gaps Plan: Spell Damage + Item Catalog

Written from a network-restricted session that couldn't reach the source APIs
to verify schemas. **Read this before writing/rewriting any generator
script** — steps 1-2 below are schema verification, not implementation, and
must happen first. Delete this file once the work below is done and folded
into the real generator scripts / CLAUDE.md.

## Background

Investigated (session ending 2026-09-27) why `ruleset_action`/`Spell` trusts
caller-supplied `damageDice` with almost no validation, and why `lookup
kind=items` is nearly empty:

- **`SpellDefinition`** (`src/CampaignVault/Data/Templates/SpellDefinition.cs`)
  has *zero* mechanical-effect fields — no damage, no save DC, no healing,
  no scaling, on any spell, in either ruleset. It only carries casting
  mechanics (level, classes, concentration, casting time, components). The
  cantrip soft-warning added this session
  (`Dnd5eRulesetResolver.BuildCantripDamageWarning`) is a hardcoded
  4-cantrip patch over this gap, not a fix to it.
- **dnd5e items**: `ItemDefinition.Properties` is a working open bag —
  `longsword.yaml` proves it (`damage: 1d8`, `damageVersatile: 1d10`,
  `damageType: slashing`). But the core catalog is two items
  (`longsword`, `climbers_kit`) plus 12 melee weapons in the
  `MedievalWeapons` plugin. Zero core armor, zero ranged weapons/ammo.
- **pf2e items**: `src/CampaignVault/RulesetData/pf2e/items/` doesn't exist.
  Zero weapons, zero armor, nothing.
- None of this is a bug in `ItemDefinitionProvider`/`SpellDefinitionProvider`/
  `RulesetTemplateLoader` — those work correctly for what YAML exists. It's a
  content-authoring/model gap, on top of which the engine (correctly, by
  design) trusts whatever `damageDice`/`acBonus` a live campaign's `Item`/
  spell cast carries, with no canonical source to check it against.

## What generates today

- `scripts/generate_spells.py` — dnd5e via `www.dnd5eapi.co` (MIT-licensed
  wrapper over SRD 5.1, CC-BY-4.0, per `LICENSING.md`); pf2e via
  `elasticsearch.aonprd.com` (Archives of Nethys, Paizo Remastered core
  rules, ORC License, per `LICENSING.md`).
- `scripts/generate_pf2e_feats.py` — same AoN endpoint, `category: "feat"`.
- **Confirmed by reading the code, not guessing**: `generate_spells.py`'s
  `load_spell()` already fetches the *full* dnd5eapi.co spell detail JSON
  (`fetch_json(f"https://www.dnd5eapi.co{entry['url']}")`) and only keeps 8
  fields off it, discarding the rest. Per the documented (stable, long-lived)
  dnd5eapi.co schema, that discarded JSON includes, on damage/save spells:
  - `damage: { damage_type, damage_at_slot_level }` (leveled spells, keyed
    by slot 1-9) or `damage: { damage_type, damage_at_character_level }`
    (cantrips, keyed by character level 1/5/11/17 — this is the exact table
    hand-written into `dnd-combat/SKILL.md` this session, except sourced
    for every scaling spell instead of 4).
  - `dc: { dc_type, dc_success }` (e.g. Fireball: `DEX`, `half`).
  - `heal_at_slot_level` for healing spells.
  - `area_of_effect: { type, size }`.
  This part is **not fetch-and-verify** — it's already fetched, just
  discarded. High confidence, no live check strictly needed, but do one
  anyway (see Step 1) since the container that writes this plan never
  actually saw a live response.
- No script has ever touched dnd5eapi.co's `/api/equipment` or
  `/api/equipment-categories/{weapon,armor,...}` endpoints, or an AoN
  document with `category` other than `spell`/`feat`. These are genuinely
  unverified — confirmed by `git log --all --grep` and reading both existing
  scripts, neither references equipment/weapon/armor AoN fields anywhere.

## Step 1 — Verify dnd5eapi.co equipment schema (cheap, do first)

```bash
curl -s https://www.dnd5eapi.co/api/equipment/longsword | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment/leather-armor | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment/shortbow | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment-categories/armor | python3 -m json.tool
curl -s "https://www.dnd5eapi.co/api/spells/fireball" | python3 -m json.tool   # confirm damage/dc shape
curl -s "https://www.dnd5eapi.co/api/spells/fire-bolt" | python3 -m json.tool # confirm cantrip shape
```
Confirm field names match what's assumed above (`damage.damage_dice`,
`2h_damage`, `armor_class.base`/`dex_bonus`/`max_bonus`, `str_minimum`,
`stealth_disadvantage`, `armor_category`). Expect this to match — flag
anything that doesn't before writing code against it.

## Step 2 — Verify AoN schema for pf2e items (must-do, not optional)

This is the actual unknown. Both existing pf2e scripts only ever query
`category: "spell"` or `category: "feat"`. Nobody in this repo's history has
seen a `category: "weapon"`/`"armor"`/`"equipment"` document, or confirmed
pf2e spell damage is a structured field vs. buried in `text`/`description`
prose.

```bash
curl -s -X POST https://elasticsearch.aonprd.com/aon/_search \
  -H 'Content-Type: application/json' \
  -d '{"size": 3, "query": {"bool": {"must": [
        {"term": {"category": "weapon"}},
        {"terms": {"primary_source.keyword": ["Player Core", "Player Core 2"]}}
      ]}}}' | python3 -m json.tool

curl -s -X POST https://elasticsearch.aonprd.com/aon/_search \
  -H 'Content-Type: application/json' \
  -d '{"size": 3, "query": {"bool": {"must": [
        {"term": {"category": "armor"}},
        {"terms": {"primary_source.keyword": ["Player Core", "Player Core 2"]}}
      ]}}}' | python3 -m json.tool

# one damage-dealing spell, full _source, to see if damage is structured
curl -s -X POST https://elasticsearch.aonprd.com/aon/_search \
  -H 'Content-Type: application/json' \
  -d '{"size": 1, "query": {"bool": {"must": [
        {"term": {"category": "spell"}},
        {"match": {"name": "Fireball"}}
      ]}}}' | python3 -m json.tool
```
If `category` isn't `weapon`/`armor` (e.g. it's `equipment` with a subtype
field instead), or the query returns zero hits, adjust before proceeding —
don't guess a second value and move on.

**If AoN's spell documents don't carry structured damage** (plausible —
Paizo/AoN spell text is often prose-first): pf2e spell damage validation may
need to stay out of scope, or need regex extraction from `text`/`description`
(fragile, lower confidence) — decide once Step 2's actual response is in
hand, not before.

## Step 3 — Extend `SpellDefinition` + `generate_spells.py` (dnd5e first)

- Add fields to `SpellDefinition.cs`: `DamageDice` (string, plain for
  non-scaling spells), `DamageAtSlotLevel`/`DamageAtCharacterLevel`
  (`Dictionary<int, string>?`), `DamageType` (string?), `SaveType`/
  `SaveSuccess` (string?), `HealDice`/`HealAtSlotLevel`, `AreaOfEffect`
  (small record or two flat fields — type/size). Extend `Merge()` to match.
  Nullable throughout — most spells (utility, buffs) have none of this.
- Extend `generate_spells.py`'s `load_spell()`/`write_spell()` (dnd5e path)
  to capture and emit these from the JSON already being fetched.
- Regenerate: `python3 scripts/generate_spells.py` (dnd5e only if pf2e schema
  isn't confirmed yet — the script already separates `generate_dnd5e()` and
  `generate_pf2e()`, can run one at a time).
- Generalize `Dnd5eRulesetResolver.BuildCantripDamageWarning` into a real
  validator: look up the cast spell's `SpellDefinition`, compare
  caller-supplied `damageDice` against `DamageAtCharacterLevel[tier]` (or
  flat `DamageDice` for non-scaling spells) for **any** spell that now has
  data, not the 4 hardcoded cantrips. Keep it a soft warning, not a hard
  fail — homebrew/reflavored spells with no definition still need to work.
  Same for saves: if `SaveType` is set and the caller's `save` param
  disagrees, warn.
- Regression tests mirroring the cantrip ones already in
  `Dnd5eRulesetResolverTests.cs` — at minimum Fireball (leveled, save,
  no scaling-by-slot-only-flat-dice-per-cast) and a slot-scaling spell like
  Magic Missile or Scorching Ray if in SRD 5.1 (Magic Missile is: 3 darts
  1d4+1 each, flat, doesn't need slot-scaling logic — pick one that actually
  exercises `damage_at_slot_level`, e.g. Burning Hands scales by slot? check
  live data since SRD 5.1's scaling spells are a smaller set than full 5e).

## Step 4 — dnd5e equipment: new `scripts/generate_items.py`

- Mirror `generate_spells.py`'s shape: fetch
  `/api/equipment-categories/{weapon,armor}`, then each `/api/equipment/{index}`.
- Map onto existing `ItemDefinition.Properties` vocabulary
  (`ArmorParameterResolver.cs`/`longsword.yaml` are the ground truth for key
  names, not the API's names):
  - Weapon: `damage` ← `damage.damage_dice`, `damageVersatile` ←
    `2h_damage.damage_dice` (when present), `damageType` ←
    `damage.damage_type.index`, `range` ← map `weapon_range`/`properties`
    (thrown/reach) to the `SpatialDistanceBand` constants
    `WeaponParameterResolver.cs` expects (Touch/Close/Near/Far/Distant) —
    this mapping doesn't exist in the API, needs a manual lookup table.
  - Armor: `acBonus` ← `armor_class.base - 10`, `armorType` ← derive
    light/medium/heavy from `armor_category` (`armor_class.max_bonus`:
    null→heavy/no cap conceptually but engine wants light=uncapped,
    medium=cap 2, heavy=cap 0 — confirm `max_bonus` values for light armor
    aren't also being clamped unexpectedly), `dexCap` ← `armor_class.max_bonus`
    when not null.
  - `EquipZones`/`EquipLayer`/`StackGroup` aren't in the API at all — these
    are this codebase's own equip-slot model (`EquipSlotRules.cs`). Every
    generated item needs these added by a lookup table keyed on
    `equipment_category`/`armor_category`, not pulled from source.
  - `Tags`: not cosmetic metadata — `Item.Tags = item.Tags ?? definition?.Tags
    ?? []` (`CampaignRepository.cs:1838`) means whatever's on the generated
    `ItemDefinition` flows onto every live `Item`, and
    `WeaponParameterResolver.NameMatches` (`WeaponParameterResolver.cs:163`)
    matches a caller's `actionName` against `weapon.Tags` as an alias list
    (so "attack with my sword" resolves via a tag, not just exact `Name`).
    Existing hand-authored items use a small controlled vocabulary
    (`martial`/`simple`, `melee`/`ranged`, `versatile`) — don't just dump
    the API's `weapon_range`/`equipment_category`/`properties` strings in
    as tags; map them onto that same vocabulary, or `NameMatches` silently
    stops resolving for generated weapons.
- Regenerate, then hand-verify a handful (longsword should regenerate
  byte-similar to today's hand-written file; that's the sanity check).

## Step 5 — pf2e items: new content, blocked on Step 2

Only after Step 2 confirms real field names. Likely needs its own
translation table for damage dice, weapon groups/traits, and
armor AC/dex-cap/check-penalty/speed-penalty fields — pf2e's model
(proficiency-based, dex-cap-by-armor-category, no versatile-damage concept)
doesn't map onto the dnd5e translation in Step 4.

`Tags` needs its own mapping too, same reasoning as Step 4's `Tags` bullet —
AoN's pf2e weapon `trait` vocabulary (Agile, Finesse, Forceful, Sweep,
Deadly, Fatal, weapon groups) is not the dnd5e martial/simple +
melee/ranged + versatile vocabulary Step 4 establishes. Don't reuse Step
4's lookup table verbatim; decide a pf2e-appropriate tag set (and keep it
consistent with whatever name-alias matching `WeaponParameterResolver`
ends up doing for pf2e weapons) before generating.

## Step 6 — Docs

- `claude_skills/dnd-combat/SKILL.md`: replace the hand-written 4-cantrip
  table with a note that the engine now validates broadly (once Step 3
  lands), and drop the manual table once it's redundant.
- `LICENSING.md`: no change expected (equipment/full spell text already
  within the stated SRD 5.1 / Player Core scope) — confirm scope still
  matches after seeing what's pulled in Step 1/2 (e.g. don't pull equipment
  images/flavor text beyond mechanical stats if any license nuance shows up).
- `CLAUDE.md`/generator script docstrings: note the two source APIs and
  that regeneration requires network access to both hosts.

## Order of execution (next session)

1. Step 1 (dnd5e schema check) — quick, do immediately.
2. Step 2 (AoN schema check) — quick, do immediately, **before** writing
   any pf2e code.
3. Step 3 (dnd5e spells) — model + script + validator + tests.
4. Step 4 (dnd5e items) — new script + regenerate + hand-verify.
5. Step 5 (pf2e items) — only if Step 2 came back usable.
6. Step 6 (docs cleanup) — last, once the data's actually in.
7. Delete this file.
