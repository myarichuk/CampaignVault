---
name: pf2e-combat
description: Pathfinder 2e combat only — load when the campaign's ruleset is pf2e (three-action turns, multiple attack penalty, degrees of success, Strikes). Do not load for D&D 5e campaigns; use dnd-combat for those.
metadata:
  type: skill
---

# Pathfinder 2e Combat

Applies **only when the campaign's ruleset is `pf2e`**. The flow in `dnd-combat` (start, turns, a PC's turn as a hard stop, the checklist) is the same here; this skill covers what differs. If the campaign is D&D 5e, ignore this file.

## The turn: three actions

- A turn is **3 actions** (budget key `actions`), not action + bonus. Every `ruleset_action` costs 1 unless it says otherwise.
- Multi-action activities (a 2-action spell, Sudden Charge) pass `parameters.actionCost` (`"2"`, `"3"`). Overspending is refused with the actions remaining.
- **Reactions** (`isReaction: true`) are free of the budget; one per round, tracked by the engine.
- There is no bonus action, no Extra Attack, no `offHand`, no Cunning Action here. Don't pass those; they are 5e.

## Strikes and the multiple attack penalty (MAP)

- **Attack** derives to-hit and damage from the sheet (ability + level + proficiency + item bonus); an explicit `bonus` / `damageBonus` replaces the derived value. Weapon proficiency defaults to *Trained*; set `attributes.weaponProficiencyRank` (2/4/6/8) on the sheet to change it.
- **The engine tracks MAP for you in tracked combat**: it counts Strikes made this turn (each `targetIds` entry is one Strike; reactions don't count) and applies 0 / −5 / −10 automatically, across separate `ruleset_action`s too. Repeat a target in `targetIds` to Strike it twice in one action.
- **Only pass `parameters.mapPenalty` to override**: agile weapons (`"4"` / `"8"`), or outside tracked combat where no turn is counted. An explicit value replaces the automatic one. Sign is ignored.
- Results carry a **degree of success** (critical success / success / failure / critical failure): ±10 from the DC/AC, a natural 20 raises it one step, a natural 1 lowers it. A critical hit doubles damage (including precision damage).
- `sneakAttack: "true"` adds Rogue precision dice when you judge the target off-guard.

## Checks, saves, spells

- **SkillCheck**: `actionName` is the skill; `parameters.dc`. A skill with no recorded modifier rolls as *untrained* (ability modifier only). Perception uses Wisdom.
- **SavingThrow**: `save` (Fortitude, Reflex, Will), `dc`. Spells with a save use the caster's spell DC if you omit `dc`.
- **Spell**: `resolution` attack / save / check / heal / utility, as in `dnd-combat`. Damage from a save is basic-save style: critical success none, success half (`halfOnSave: "false"` for full), failure full, critical failure double — the engine applies it.
- **Grapple / Escape**: `ContestedCheck` with `skill` (default Athletics). Grapple is against Fortitude DC, Escape against 10 + the grabber's bonus in the same `skill`; success auto-applies the engagement. Pass `dc` to override.
- Focus points and slots are resource pools (`focus_points`, `spell_slots_N`); commit the `resource` spend in the same batch.

## Feats

Homebrew and SRD feats with `effects` fold into Strikes, damage, skills, saves and AC exactly as in `dnd-combat`'s "Feat effects" section. PF2e differences:

- Effects carry a **`bonusType`** (`circumstance`, `status`, `item`, `untyped`). Among a character's *feat* effects only the highest bonus and worst penalty of each typed kind count; untyped stack.
- Judged conditions ("an ally is flanking", "target is off-guard") go in `parameters.assert`; `take_turn`'s `featChecklist` lists them. Never invent the number.
- Reactions and triggered feats fit no effect kind: keep them in `mechanicalSummary` with `adjudicated: true` and apply them by judgment.

## Not echoed / not enforced

- Contested-check and spell-save target notes from feat effects are not echoed in the result.
- Automatic MAP assumes non-agile weapons; pass `mapPenalty` for agile. The three-action cost of activities is only as good as the `actionCost` you pass.
- Conditions (frightened, off-guard, prone) are `status` commits; the engine folds their listed modifiers into rolls but does not know PF2e condition rules by name.
