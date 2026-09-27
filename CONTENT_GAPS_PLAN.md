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

## Step 1 — Verify dnd5eapi.co equipment schema (cheap, do first) — ✅ DONE (2026-09-27)

```bash
curl -s https://www.dnd5eapi.co/api/equipment/longsword | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment/leather-armor | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment/shortbow | python3 -m json.tool
curl -s https://www.dnd5eapi.co/api/equipment-categories/armor | python3 -m json.tool
curl -s "https://www.dnd5eapi.co/api/spells/fireball" | python3 -m json.tool   # confirm damage/dc shape
curl -s "https://www.dnd5eapi.co/api/spells/fire-bolt" | python3 -m json.tool # confirm cantrip shape
```

**Findings (live-queried, not assumed):**
- The API now serves versioned routes under `/api/2014/...`; the unversioned
  paths above 301-redirect. `curl -s` doesn't follow redirects (hence empty
  bodies on the first pass — re-ran with `-L`), but this is a non-issue for
  the actual generator: `generate_spells.py`'s `fetch_json()` uses
  `urllib.request.urlopen`, which follows redirects by default, and every
  `url` field the API returns in list responses is already `/api/2014/`-
  prefixed. No code change needed here.
- **Correction to this plan's own assumption**: the two-handed-damage field
  is named `two_handed_damage`, not `2h_damage` as written above in Step 4.
  Use the real name when Step 4 is implemented.
- Everything else matched: `damage.damage_dice`, `damage.damage_type.index`,
  `armor_class.base`, `armor_class.dex_bonus` (bool), `armor_class.max_bonus`
  (present only on Medium armor in the sample — absent on Light because
  uncapped, absent on Heavy because `dex_bonus: false` makes it moot),
  `str_minimum`, `stealth_disadvantage`, `armor_category`, `weapon_range`.
- Spell schema confirmed structured exactly as assumed: `damage[].damage_type`
  + `damage[].damage_at_slot_level` (leveled, e.g. Fireball: `{"3":"8d6",
  "4":"9d6",...}`) or `damage[].damage_at_character_level` (cantrips, e.g.
  Fire Bolt: `{"1":"1d10","5":"2d10","11":"3d10","17":"4d10"}`), `dc.dc_type`
  + `dc.dc_success`, `area_of_effect.type`/`.size`. Step 3 can proceed as
  written for dnd5e.

## Step 2 — Verify AoN schema for pf2e items (must-do, not optional) — ✅ DONE (2026-09-27)

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

**Findings (live-queried against `elasticsearch.aonprd.com`, scoped to
`primary_source.keyword: ["Player Core", "Player Core 2"]`):**
- `category: "weapon"` and `category: "armor"` both exist and return real,
  structured hits (83 weapons, 13 armors in Player Core/PC2 alone) — the
  plan's pessimistic branch ("adjust before proceeding") doesn't apply.
- **Weapon fields**: `damage` (combined string, e.g. `"1d8 P"` — not
  pre-split), `damage_die` (int), `damage_type` (list, usually one entry),
  `weapon_category` (`Simple`/`Martial`/`Unarmed`/`Advanced`), `weapon_group`,
  `weapon_type` (`Melee`/`Ranged`), `trait` (list — Agile/Finesse/Thrown/
  Versatile S etc.), `hands`, `bulk`, `price` (int, copper-ish base unit —
  confirm denomination before use), `level`. Ranged-only: `range` (int, feet)
  and `reload` (int, absent on non-reload weapons like thrown Darts).
  `deity`/`deity_markdown` (favored-weapon lists) are large and irrelevant —
  exclude via `_source` filtering per CLAUDE.md's query-layer-filtering rule,
  don't fetch-then-discard.
- **Armor fields**: `ac` (int bonus, not `armor_class.base` — different shape
  than dnd5e), `armor_category` (`Unarmored`/`Light`/`Medium`/`Heavy`),
  `dex_cap` (int, **absent** — not null, the key is missing entirely — when
  uncapped, e.g. Unarmored), `check_penalty` (int, negative, absent when 0),
  `speed_penalty` (string like `"-5 ft."`, absent when none — not numeric),
  `strength` (int, str requirement, `0` is a real value distinct from
  absent), `bulk`, `price`, `armor_group`, `trait`.
- **Critical finding — pf2e spell damage is NOT structured.** Unlike dnd5e,
  AoN spell documents have no `damage_dice`/`damage_type`/`dc` fields at all.
  Damage only exists inside prose: Fireball's `text`/`markdown` says
  "dealing 6d6 fire damage" and "Heightened (+1) The damage increases by
  2d6." Extracting that reliably means regex/parsing over free text — the
  exact failure mode (`PREREQ_RE`) this session just spent its whole budget
  eliminating from the feats generator. Per that same reasoning, **pf2e
  spell damage validation stays out of scope for Step 3** rather than
  reintroducing prose-regex fragility one file over. dnd5e spell damage
  (Step 3) is unaffected and should proceed as planned since dnd5eapi.co's
  damage fields are genuinely structured (see Step 1 findings).
- **Confirms the scope filter is load-bearing, not decorative**: querying
  `category: "spell"` + `match: "Fireball"` with no `primary_source` filter
  returns the pre-Remaster `"Core Rulebook"` Fireball (`spell-119`), not the
  Player Core one — it carries a `remaster_id: ["spell-1530"]` field pointing
  at the actual remastered doc. `generate_spells.py`'s existing
  `primary_source.keyword: ["Player Core", "Player Core 2"]` filter is what
  keeps this pipeline off legacy (non-ORC) content; Step 4/5 work must keep
  the same filter on every AoN query, not just spells/feats.

**If AoN's spell documents don't carry structured damage** (plausible —
Paizo/AoN spell text is often prose-first): pf2e spell damage validation may
need to stay out of scope, or need regex extraction from `text`/`description`
(fragile, lower confidence) — decide once Step 2's actual response is in
hand, not before.

## Step 3 — Extend `SpellDefinition` + `generate_spells.py` (dnd5e only — pf2e out of scope, see Step 2 findings) — ✅ DONE (2026-09-27)

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

## Step 4 — dnd5e equipment: new `scripts/generate_items.py` — ✅ DONE (2026-09-27)

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

**Findings (live-queried against `www.dnd5eapi.co`):**
- Both `equipment-categories/{weapon,armor}` listings interleave magic items
  (Vorpal Sword, Armor +1, Dragon Scale Mail, ...) whose `url` points at
  `/api/2014/magic-items/...` instead of `/api/2014/equipment/...` — 30 of 67
  weapon-category entries, 29 of 42 armor-category entries. These carry no
  fixed SRD stat block (bonuses vary per named item) and were filtered out
  by checking the entry's own `url` prefix, not by guessing at names. Yields
  37 mundane weapons + 13 mundane armor/shields — matches the plan's own
  pessimistic branch never triggering (same shape as Step 2's weapon/armor
  finding).
- Confirmed `two_handed_damage` (not `2h_damage`, per the Step 1 correction)
  and that it's only meaningful alongside the `versatile` property — some
  weapons carry no `properties` array element for it at all.
- **Shield is armor_category `"Shield"`, not `Light`/`Medium`/`Heavy`, and its
  `armor_class.base` (2) is already a flat AC bonus, not a "10 + dex" target
  AC** like body armor. Applying the plan's own `acBonus = base - 10` formula
  to a shield gives `acBonus = -8`, which is wrong — Shield is special-cased
  to `acBonus = base` directly. This wasn't visible from Step 1's
  body-armor-only sample and was only caught by generating and reading the
  actual shield output.
- `dexCap` (plan's Step 4 wording) turned out unnecessary for dnd5e: every
  Medium armor has `max_bonus: 2` and every Light/Heavy has none, which is
  *exactly* what `ArmorParameterResolver`'s existing `armorType` fallback
  (medium→cap 2, heavy→cap 0, else uncapped) already encodes. Writing a
  redundant numeric `dexCap` would just duplicate logic the resolver already
  has; that property is reserved for pf2e's own numeric convention (Step 5).
- `EquipZones`/`twoHanded` were **not** modeled after the MedievalWeapons
  plugin's own convention (which gives even one-handed weapons
  `equipZones: [MainHand, OffHand]`, fully occupying both hand slots and
  making sword-and-shield or dual-wielding impossible for any plugin
  weapon) — that reads as a plugin-authoring inconsistency, not a pattern to
  propagate. Generated weapons instead use `equipZones: [MainHand]` +
  `twoHanded` driven directly by the SRD `two-handed` property, relying on
  `EquipSlotRules.GetEffectiveZones`'s own documented implicit-OffHand
  expansion for genuinely two-handed weapons — this is what actually lets a
  versatile weapon (e.g. generated `longsword.yaml`) be worn with a shield.
- The 5ft-reach "Touch"/mid-range "Close"/... `SpatialDistanceBand` banding
  for thrown/ranged weapons has no source-provided mapping to feet; picked
  break points (≤5/≤30/≤80/≤150/else) so real SRD weapons land where the
  existing hand-authored convention already expects (e.g. thrown daggers at
  throw-normal 20ft → `Close`, matching `medieval_hand_axe.yaml`'s
  `range: Close # Throwable`). Standard melee weapons get no `range` key at
  all, same as the pre-existing `longsword.yaml` — `RangeValidationHelper`
  is permissive when absent, so omitting it isn't a gating gap.
- `RulesetData/dnd5e/items/` is a mixed hand-authored/generated directory
  (`climbers_kit.yaml` is a Tool, never touched — Tool isn't a weapon/armor
  category SRD exposes), so unlike `generate_spells.py`/
  `generate_pf2e_feats.py`, `generate_items.py` does **not** wipe the
  directory first; it only overwrites the specific slugs it generates.
  `longsword.yaml` is one of those slugs (SRD has a weapon of the same
  name) and gets regenerated — its mechanical stats came out
  byte-identical, plus it gains equip metadata (`equipZones`/`equipLayer`/
  `twoHanded`) the hand-authored version never had, meaning today's core
  longsword currently *can't* be equipped via `item_equip`. Regenerating it
  is a fix, not a regression.
- Usage/setup instructions for all three generator scripts now live in
  `scripts/README.md`.

## Step 5 — pf2e items: new content — ✅ DONE (2026-09-27)

Likely needs its own
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

**Findings (live-queried against `elasticsearch.aonprd.com`, `scripts/generate_pf2e_items.py`):**
- **AC math is fundamentally simpler for pf2e than the dnd5e translation in Step
  4, not just different.** A pf2e weapon/armor/shield's own `ac` field IS already
  the flat AC-bonus contribution (`ArmorClass = 10 + effectiveDex + proficiency +
  acBonus`) — there's no dnd5e-style "10 + dex" total-AC-at-zero-dex shape to
  subtract 10 from, for body armor *or* shields. `acBonus = ac`, unconditionally,
  no special-casing needed (contrast Step 4, where dnd5e Shield needed a
  special case specifically because body armor *isn't* a flat bonus there).
- `dex_cap` is an explicit numeric field on pf2e armor, present only when capped
  (confirmed: absent — not null, the key is missing — on Unarmored's placeholder
  entry and would be absent on any hypothetical uncapped armor; present as `0` on
  Full Plate, a real zero distinct from absent). Written straight to
  `Properties["dexCap"]`, which is exactly the "PF2e style" numeric convention
  `ArmorParameterResolver.cs`'s own comment reserves that key for — the dnd5e-only
  `armorType` fallback key is never written by this script.
- **Correction to this plan's own "no versatile-damage concept" assumption**:
  pf2e *does* have one — the `Two-Hand` trait (e.g. Bastard Sword: `1d8` one-handed
  base, `trait_raw: "Two-Hand 1d12"` for wielding it in both hands) behaves exactly
  like dnd5e's Versatile and is captured the same way (`damageVersatile`,
  `equipZones: [MainHand]`, `twoHanded: false` — relying on
  `EquipSlotRules.GetEffectiveZones`'s implicit-OffHand expansion only when
  actually wielded two-handed, same mechanism Step 4 uses). This was caught live,
  not assumed, by grepping the full 83-weapon `trait_raw` vocabulary for
  numeric-suffixed traits before writing the generator.
- **Shields are their own AoN document category (`category: "shield"`), not a
  subtype of `armor`** like dnd5e's `armor_category: "Shield"` — this plan's own
  Step 2 never surfaced it because Step 2 only ever queried `category: "weapon"`
  and `category: "armor"`. Found by noticing the 4 real base shields
  (Buckler/Wooden/Steel/Tower Shield, with their own `ac`/`hardness`/`hp` fields)
  are absent from both `category: "armor"` (13 hits, all body armor) and
  `category: "equipment"`'s `item_category: "Shields"` bucket (which holds only
  precious-material variants and specific magic shields with no base stats of
  their own — deferring to a base shield document this script wouldn't otherwise
  have found). `generate_pf2e_items.py` queries all three categories.
- AoN's `trait` field (already stripped of numeric/qualifier suffixes, e.g.
  `"Thrown 10 ft."` → `"Thrown"`, `"Versatile S"` → `"Versatile"`, `"Two-Hand
  1d12"` → `"Two-Hand"`) is used directly for `Tags`, and `trait_raw` (the
  unstripped form) only for regex-extracting the two numeric values that matter
  mechanically (`Thrown N ft.` for range-banding, `Two-Hand NdM` for
  `damageVersatile`) — confirmed this split exists and is reliable across the
  full 83-weapon trait vocabulary before relying on it, avoiding the free-prose
  regex fragility this session already eliminated from the feats generator.
- Scoped to `rarity: common` (same filter `generate_pf2e_feats.py` already uses,
  not previously applied to weapons/armor) — this is load-bearing, not
  decorative: it naturally drops the 24 Uncommon weapons (mostly ancestry-specific
  weapons like Dwarven Waraxe/Gnome Hooked Hammer, which need an ancestry feat to
  use without penalty) without needing separate ancestry-aware filtering logic.
  All 13 armors and all 4 shields happened to already be common.
- Excluded `weapon_category: "Unarmed"` (Fist — not carried gear, always
  innately available, no price/bulk) and `weapon_category: "Ammunition"` (Arrows/
  Bolts/Sling Bullets/Blowgun Darts — consumable ammo stacks, not held weapons),
  by the same "out of scope" reasoning Step 4 used implicitly for dnd5e ammo
  (`generate_items.py` never queried dnd5eapi.co's ammunition equipment category
  either). Also excluded "Alchemical Bomb", a generic weapon-group placeholder
  entry (`damage: "Varies"`, no `damage_die` key at all) for the whole alchemical-
  bombs family — real bombs are separate Equipment/Consumable documents elsewhere.
  Filtered by the presence of `damage_die`, not by name.
- **Known modeling gap, left as-is rather than silently patched over**: three
  weapon-group-`"Shield"` entries (Shield Bash, Shield Boss, Shield Spikes) let a
  character strike with a shield *already* worn in `OffHand` — they cost no
  separate hand in real pf2e rules, and Shield Boss/Spikes additionally carry an
  `Attached` trait (`trait_raw: "Attached to Shield"`) meaning they're a
  permanent modification to the shield object, not a separately-drawn weapon.
  This engine has no "attached to another equipped item" equip concept (`
  ItemDefinition.RequiresEquippedTags` exists but its enforcement wasn't
  traced/verified as part of this content-generation task, so wiring it here
  would be guessing, not fixing). They're generated as ordinary `equipZones:
  [MainHand]` weapons like any other — harmless for attack resolution
  (`WeaponParameterResolver.GetHeldWeaponsAsync` matches on `HolderId` +
  `CoreCategory` only, not on equip-zone occupancy), but calling `item_equip` on
  one while a real weapon already occupies MainHand will (correctly, per today's
  `EquipSlotRules` zone-conflict semantics) report a conflict that doesn't match
  the pf2e fluff of "costs no hand." Flagging this explicitly rather than leaving
  it to be discovered as a confusing bug later.
- 69 items generated: 53 weapons, 12 armor (13 pf2e armor documents minus the
  "Unarmored" placeholder, which — like dnd5e's Shield acBonus catch in Step 4 —
  was only caught by checking for entries lacking a `price` key: it's `ac: 0`,
  literally no armor, not real equipment), 4 shields.
- `RulesetData/pf2e/items/` didn't exist before this script (confirmed: `ls`
  404s) — unlike Step 4's dnd5e `items/` directory, there's no hand-authored
  content to preserve, so `generate_pf2e_items.py` fully wipes and rebuilds the
  directory every run, same convention as `generate_spells.py`/
  `generate_pf2e_feats.py` rather than Step 4's non-destructive slug-scoped
  approach.
- `Properties` key vocabulary deliberately diverges from Step 4's dnd5e
  convention where pf2e's own source semantics differ — `bulk` (not `weight`,
  pf2e's Bulk is an abstracted carry-capacity unit, not pounds) and `strength`
  (not `strMinimum`) are named after pf2e's own field names rather than reusing
  dnd5e's, since `ItemDefinition.Properties` is an open per-`System` bag with no
  cross-ruleset key-sharing requirement.
- Usage/setup instructions added to `scripts/README.md` alongside the other two
  generators.

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

## Order of execution

1. ~~Step 1 (dnd5e schema check)~~ — done 2026-09-27.
2. ~~Step 2 (AoN schema check)~~ — done 2026-09-27. Items unblocked; pf2e
   spell damage validation is now explicitly out of scope (prose-only,
   would need the same regex fragility this session removed from feats).
3. ~~Step 3 (dnd5e spells)~~ — done 2026-09-27. Model + script + validator +
   tests, commit f9247db.
4. ~~Step 4 (dnd5e items)~~ — done 2026-09-27. `scripts/generate_items.py`
   (37 weapons + 13 armor/shields), `scripts/README.md` written for all
   three generators.
5. ~~Step 5 (pf2e items)~~ — done 2026-09-27. `scripts/generate_pf2e_items.py`
   (53 weapons + 12 armor + 4 shields, 69 total), `scripts/README.md` updated.
6. Step 6 (docs cleanup) — last, once the data's actually in. Not started.
7. Delete this file.
