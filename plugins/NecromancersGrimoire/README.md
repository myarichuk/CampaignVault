# Necromancer's Grimoire

Homebrew necromancy content for dnd5e (`ng_` prefix): damage spells that work
with the engine today, undead/outsider creature seeds, minion armaments, an
onyx focus consumable, and one engine-driven summon spell.

## Contents

- `RulesetData/dnd5e/spells/ng_*.yaml` — 10 damage spells (cantrip through
  6th level, single-target / save / AoE / delayed-tick delivery) plus
  `ng_bind_shade`, a 2nd-level concentration summon with a machine-enforced
  control cap (`controlCap.maxCreatures: 2`).
- `RulesetData/dnd5e/creatures/ng_*.yaml` — 7 seeds: skeletal archer, zombie
  brute, ghast hound, umbral stalker (the shade `ng_bind_shade` raises),
  cinder wisp, impish trickster, wight blade.
- `RulesetData/dnd5e/items/ng_*.yaml` — onyx focus (25 gp consumable, the
  material currency for binding), shortbow, bone dagger, greatclub. Raised
  minions spawn armed when the same `world_build` batch includes their
  armament.

## Control-cap prose

`ng_bind_shade` enforces its two-shade cap in the engine; `animate_dead`
retains narratively. Suggested table rule (prose until a standing HD cap
ships): one caster controls at most 4 hit dice of undead per caster level —
tracked via the onyx focus expenditure.
