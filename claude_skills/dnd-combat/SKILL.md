---
name: dnd-combat
description: Combat flow shared by every ruleset — starting and advancing combat, the eight action types, reactions, restraint, status effects, range and feat effects. Load with dnd5e-combat (D&D 5e) or pf2e-combat (Pathfinder 2e) for the system's own rules.
metadata:
  type: skill
---

# Combat (shared flow)

What every ruleset shares. The system's own rules (attack flags, spells, slots, components, dying and rest) are in `dnd5e-combat` for D&D 5e and `pf2e-combat` for Pathfinder 2e: load this skill and the one for the campaign's ruleset, never both system skills.

## Flow

1. **Start:** `combat(action: "start", locationId, combatantIds)`; everyone rolls initiative once.
2. **Turns:** `combat(action: "next")` advances the turn and expires round-based statuses.
3. **Every action is a `ruleset_action`** in `take_turn`. The engine rolls and applies damage, healing, conditions and grapple engagement itself; never add a manual `hp`, `status` or `engagement_relation` for the same character in the same batch (`dnd-world-change`).
4. **Grapple and shove:** a `ContestedCheck` whose `actionName` says grapple or shove (or `parameters.maneuver: "grapple"`); success applies the engagement. Escaping is `parameters.maneuver: "escape"`. A manual `engagement_relation` is only for restraint that isn't a grapple.
5. **A PC's turn is a hard stop.** When the turn lands on a PC, describe the round state (what changed, who threatens whom, what is ticking) and wait for the player's declared action. Never choose their target, action or spell, and never run a PC's turn into the next NPC's without their input. An NPC's turn is resolved and narrated in one batch, from their psychology and `TurnIntent`.
6. **A legal action is attempted, not refused.** A low chance to hit is the roll's business.
7. **End:** `combat(action: "end")`.

Reactions and opportunity attacks are a `ruleset_action` with `isReaction: true`; a reaction is spent until the combatant's own next turn starts.

## Action types (`ruleset_action.actionType`)

- **Attack**: `actionName` is the weapon; send its dice in `parameters.damageDice` without a modifier and omit `bonus` / `damageBonus`: the engine derives them from the sheet, and passing either replaces the derived total. One target per swing in `targetIds`.
- **SkillCheck**: `actionName` is the skill; `parameters.dc`. `parameters.skill` only to roll under a different skill than the label.
- **SavingThrow**: `parameters.save` (the ability or save name) and `dc`.
- **ContestedCheck** / **OpposedCheck**: the actor's skill against the target's (grapple, shove, escape, a race, hide against search); the target's skill is `parameters.defenderSkill` (5e) or `targetSkill`. Both types resolve the same way.
- **Spell**: `actionName` is the spell; `parameters.resolution` is attack, save, check, heal or utility.
- **Recovery**: a self-heal feature (Second Wind needs nothing else; other features pass `healDice`/`healBonus` or `healAmount`).
- **UseItem**: a consumable or device; a healing one passes `healDice` or `healAmount`, anything else is reported with no HP change.

Anything the engine applies is reported in the result; nothing rolls on its own.

## Restraint that isn't a grapple

Holding someone down after the fight, tying them, escorting a prisoner: commit it yourself.
```json
{ "$type": "engagement_relation", "characterId": "chars/fighter", "targetId": "chars/goblin", "category": "Physical", "verb": "restraining" }
```

## Status Effects

The engine applies a condition when an action causes it (a failed save, a grapple, 0 HP: Unconscious and Prone, an Exhaustion level). One it didn't apply, such as a zone's effect or a curse, is a `status`; ending it is `status_remove`:
```json
{ "$type": "status", "characterId": "chars/wizard", "status": "Cursed" }
```

## Range

`range` is the normal maximum band, `longRange` lets a farther target be hit with disadvantage, and `minRange` refuses a closer one. A ranged weapon attack with a living hostile within Touch of the attacker has disadvantage. An area spell's `range` limits its origin point (measured from the caster); `aoeRadius` is measured from the origin. Nothing is checked until positions are recorded. The engine sets the disadvantage; don't also pass it.

## Feat effects (homebrew and SRD)
Feats can declare `effects` (seeded through `world_build` `feats[]`): fixed numeric modifiers to attack, damage, skill, save or AC rolls. The engine applies the number; you only supply facts.
- **Engine-checked** conditions (weapon `ranged`/`melee`/`finesse`/`twoHanded`/`heavy`) and player **toggles** (`parameters.powerAttack: "true"`, `parameters.twoHanded: "true"` for a versatile weapon's larger die) need nothing from you beyond the toggle. A spell's casting time sets its budget (bonus action, reaction) and a touch spell counts as a melee attack; a spell of a minute or more is refused in combat.
- **DM-judged** conditions ("an ally is adjacent", "shooting from higher ground") are flags: put the ones you judge true right now in `parameters.assert` (comma-separated, e.g. `"allyNear,highGround"`). take_turn's `featChecklist` lists the flags every response while combat is active (everyone in round 1, the active character after). An effect whose flag you did not assert is *reported* in the roll result ("not applied: needs assert=…"), never applied silently. Never invent the number; pass only the flag.
- PF2e effects carry a `bonusType`; among a character's feat effects only the highest bonus and worst penalty of each typed kind count.
- A feat gated with `requires: {plugin, mode?}` is inert until that plugin is loaded (and that mode running, if named).
- A homebrew feat with no effects is flagged `feat_unimplemented_effects`; set `adjudicated: true` if you will apply it by judgment. Rules that fit no effect kind (reactions, triggers) belong in `mechanicalSummary` with `adjudicated: true`.
- Roll results echo every applied or unclaimed effect (attack, damage, skill, save, AC, PF2e grapple/escape). Not yet echoed: PF2e contested checks and spell saves against a target, and 5e/PF2e initiative (initiative effects are not supported).
