# Summoning TODO — spell summon effects + minion-control link

Deferred feature design (no implementation yet). Covers PC necromancers and
summoners, NPC/enemy summoners, and every summon family: elementals, demons
and fiends, fey and celestials, beasts, and raised undead.

## Current state (verified 2026-09-27)

- Summon spells exist as **metadata only**: dnd5e `animate_dead`,
  `conjure_animals/celestial/elemental/fey/minor_elementals/woodland_beings`,
  `animate_objects`, `instant_summons`; pf2e `summon_animal/celestial/
  construct/dragon/elemental/entity/fey/fiend`, `animated_assault`,
  `rouse_skeletons`. `SpellDefinition` carries damage/save/heal/AoE and
  nothing else — casting one validates the slot spend and leaves the rest
  to narration.
- Live creatures are ordinary `Character` docs created via `world_build`
  (with `items[]` in the same batch, or they spawn unarmed). Creature YAMLs
  (`skeleton`, `zombie`, `giant_spider`, …) are DM handbook reference only.
- No minion concept exists anywhere: no `controlledBy`/`masterId`/
  `summonedBy` field, no summon effect type, no control cap. Loyalty,
  control limits ("4 HD per level"), and dismissal are all narrated.
- Reusable mechanics already present: `spell_slots_*` pools + validation,
  `concentration` flags on spells, `StatusEffect.ExpiresAtDay/
  ExpiresAtRound` + `StatusExpiryRule`, `poisoned`-style conditions.

## Proposed engine support

### 1. Summon effect on spells (data shape, dnd5e-first)

New optional block on `SpellDefinition`, e.g. `summon:` with:

- `creature`: handbook reference (creature definition name) or inline
  stat seed (hp, defense, attacks) for homebrew summons.
- `countAtSlotLevel`: `{3: 1, 5: 2, …}` — more/harder summons upcast.
- `durationRounds` / `durationDays`: concentration-bound (conjure/summon)
  vs indefinite (animate dead, until dispelled or destroyed).
- `controlCap`: max controlled HD/creatures, e.g. animate dead's
  "4 HD per necromancer level" — enforced at cast, excess oldest-first
  released (narrated or auto-dismissed).
- `disposition`: `loyal` (undead under control, eidolons), `neutral`
  (elementals bargaining), `hostile` (demons escaping a broken circle).

### 2. Minion-control link on the caster (any Character, not PC-only)

- New nullable fields on `Character` (or a sidecar record):
  `controlsMinionIds: [ids]` on the caster, `controlledById` on the minion.
- Enemy summoners use the identical link — a cultist's demon and a PC's
  skeleton are the same data shape; only the disposition differs.
- Rules: minion acts on the caster's turn via ordinary `ruleset_action`
  (no new action type); dismiss/release is a commit removing the link
  (body stays as an ordinary NPC/corpse per disposition); caster death or
  broken concentration flips `loyal` minions to `neutral`/`hostile`.
- Concentration hook: damage to a concentrating caster risks dropping
  concentration-bound summons (reuse the existing `concentration` flag as
  the marker; add the break check).

### 3. Content pack (deferred "Necromancer's Grimoire", plugin)

Data-only, shippable before/independently of §1–§2:

- ~10 homebrew damage necromancy spells (fully mechanical today):
  bone spear, spirit siphon, grave-chill cantrip, …
- ~6–8 undead/outsider creature seeds (skeletal archer, zombie brute,
  ghast hound, lesser elemental, impish trickster, …) with matching
  `items[]` so raised minions spawn armed.
- Onyx focus `Consumable` (animate dead's 25 gp/HD material has a real
  `MaterialCost` field) + control-cap prose until §1 enforces it.
- All `ss_`-prefixed in a plugin: core `spells/` dirs are wiped and
  regenerated, so homebrew spells must never live in core.

## Coverage checklist (all families, both sides of the table)

- [ ] Elemental summons (conjure/summon elemental, hostile-if-unbound)
- [ ] Demon/fiend summons (binding circle + escape disposition)
- [ ] Fey/celestial summons (bargain disposition)
- [ ] Beast/nature summons (conjure animals path)
- [ ] Undead raising, short-term (danse macabre style) and permanent
      (animate dead + control cap + recasting to retain)
- [ ] Enemy summoners: cultist ritual, necromancer villain with retinue,
      dismissal/kill-the-caster counterplay
- [ ] PC summoners: control cap UI/narrative, minion turns, dismissal

## Acceptance sketch

- Cast → slot validated → minion `Character`(s) created from seed,
  link set both ways, cap enforced.
- Minion acts via standard actions; GM sees "controlled by X (loyal)".
- Concentration break / caster death / cap overflow each produce the
  documented disposition transition, surfaced as a narrative prompt.
- Enemy caster + PC caster paths share one implementation.

## Non-goals

- No full monster manual (seeds on demand, not exhaustive stat blocks).
- No mass-combat/army rules — single-digit retinue scale only.
- No setting Product Identity (no named demon lords, no drow priestesses).

## Estimate (rough, revisit at implementation)

- Content pack (§3): ~25 files, one focused session, no engine risk.
- Engine support (§1–§2): 3–5× the content-pack cost; multi-phase
  (schema → link + cap → concentration/expiry hooks → tests).
  Recommended order: content pack first (~80% of the fantasy), engine
  after, if the narrative-only control loop proves insufficient.
