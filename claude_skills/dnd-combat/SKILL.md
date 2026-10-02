---
name: dnd-combat
description: D&D 5e combat (Pathfinder 2e campaigns use pf2e-combat) — initialization, turn order, actions, spells, HP, grapple, and status effects
metadata:
  type: skill
---

# Combat (D&D 5e)

For Pathfinder 2e campaigns, `pf2e-combat` covers what differs; the flow below is shared.

## Flow

1. **Start:** `combat(action: "start", locationId, combatantIds)`; everyone rolls initiative once.
2. **Turns:** `combat(action: "next")` advances the turn and expires round-based statuses.
3. **Every action is a `ruleset_action`** in `take_turn`. The engine rolls and applies damage, conditions and grapple engagement itself (`dnd-world-change`).
4. **Grapple and shove:** `ContestedCheck` with `Maneuver`; success applies the engagement. A manual `engagement_relation` is only for restraint that isn't a grapple.
5. **A PC's turn is a hard stop.** When the turn lands on a PC, describe the round state (what changed, who threatens whom, what is ticking) and wait for the player's declared action. Never choose their target, action or spell, and never run a PC's turn into the next NPC's without their input. An NPC's turn is resolved and narrated in one batch, from their psychology and `TurnIntent`.
6. **A legal action is attempted, not refused.** A low chance to hit is the roll's business.
7. **End:** `combat(action: "end")`.

Reactions and opportunity attacks are a `ruleset_action` with `isReaction: true`.

## Action Types (ruleset_action.actionType)

- **Attack** — send the weapon's dice in `damageDice` without a modifier; omit `bonus`/`damageBonus`: the engine derives them from the sheet (ability + proficiency + fighting style + weapon enchantment); passing either replaces the derived total. Optional per-attack flags: `powerAttack` (only if a recorded feat defines that toggle), `sneakAttack` (ally adjacent), `offHand` (bonus-action off-hand attack; no ability mod on damage without Two-Weapon Fighting; refused before an Attack action this turn, and, when weapons are equipped in the hand zones, unless two light weapons are wielded — `item_equip` both), `actionSurge`, `bonusAction`, `assert` (see below). List one target per swing of Extra Attack; the result warns if `targetIds` outnumber the attacks one action allows or `attackCount` skips a listed target. Cunning Action is its own bonus action: `actionName` "Cunning Action", `parameters.option` dash|disengage|hide (hide needs `dc`). Bard Jack of All Trades is applied automatically to unproficient ability checks and initiative. Extra Attack (Fighter 5+) needs no flag: the further swings of one Attack action don't cost another action. Record fighting style/subclass via level_up choices or `systemStats.levelUpChoices`, or Archery/Dueling won't apply.
- **SkillCheck** — `actionName` is the skill; `parameters.dc`
- **SavingThrow** — `parameters.save` (Dexterity, etc.) and `dc`
- **ContestedCheck** — skill vs. skill (for grapple, opposed rolls)
- **Spell** — spell name, resolution (attack/save/check/heal/utility), parameters

## Spell Examples

**Fire Bolt** (attack):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/wizard",
  "targetIds": ["chars/goblin"],
  "actionType": "Spell",
  "actionName": "Fire Bolt",
  "parameters": { "resolution": "attack", "bonus": 5, "damageDice": "1d10" }
}
```

**Cantrip damage scales with character level, not spell level:** 1 die at levels 1–4, 2 at 5–10, 3 at 11–16, 4 at 17–20, and no ability modifier on the damage. Every SRD spell carries its damage and save data, so the engine warns when the dice or save you send disagree with the spell; the damage still applies as sent, and homebrew spells cast normally.

**Fireball** (save, all targets):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/wizard",
  "targetIds": ["chars/goblin-1", "chars/goblin-2", "chars/goblin-3"],
  "actionType": "Spell",
  "actionName": "Fireball",
  "parameters": { "resolution": "save", "dc": 15, "save": "Dexterity", "damageDice": "8d6" }
}
```

**Multi-pool saves** (Ice Storm, Meteor Swarm, Flame Strike) deal two typed pools at once — send the summed total and the engine rolls each pool separately:
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/druid",
  "targetIds": ["chars/ogre"],
  "actionType": "Spell",
  "actionName": "Ice Storm",
  "parameters": { "resolution": "save", "dc": 15, "save": "Dexterity", "damageDice": "2d8+4d6" }
}
```
Pool order and pre-combining don't matter (`4d6+2d8`, `40d6` for Meteor Swarm all match). One pool alone (`2d8`) warns — that's the old flat-table half-total, not the spell's real damage. Flame Strike cast above 5th adds `upcastPool` naming which pool grows: `"damageDice": "9d6", "upcastPool": "radiant"` (fire or radiant, your choice per the spell).

**Healing Word** (heal):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/cleric",
  "targetIds": ["chars/rogue"],
  "actionType": "Spell",
  "actionName": "Healing Word",
  "parameters": { "resolution": "heal", "healDice": "1d4", "healBonus": 3 }
}
```

**Utility spells** (Mage Armor, Alarm) roll nothing and apply nothing: commit the lasting effect as a `status` (with `statModifiers`, for example ArmorClass) and the slot as a `resource` in the same batch.

## Spell Components

Before resolving a `Spell` action, check whether the caster can actually supply what the spell requires:
- **Verbal** — can the caster speak? Gagged, silenced, or similar conditions block this.
- **Somatic** — does the caster have a free hand/gesture available? Bound or fully-occupied hands block this (unless a feat waives it).
- **Material** — does the caster possess the required component/focus, or is it a costly component they're carrying?

When *you* apply a status effect (Gagged, Bound, silence zone, etc.) that should block a component, tag it so the engine catches it on later turns: set `BlocksVerbalComponents`, `BlocksSomaticComponents`, `BlocksMaterialComponents`, or `BlocksAllActions` (any nonzero value) in that status effect's `statModifiers` (lifting feat/effect uses the matching `Waives*` key). A `[SpellcastingBlocked]` rejection is a legitimate outcome — narrate why, don't retry around it.

Casting also spends the slot — commit `{ "$type": "resource", "characterId": "chars/wizard", "poolName": "spell_slots_3", "delta": -1 }` in the same batch. Overspend hard-fails; narrate the fizzle and let the player pick another.

## Restraint that isn't a grapple

Holding someone down after the fight, tying them, escorting a prisoner: commit it yourself.
```json
{ "$type": "engagement_relation", "characterId": "chars/fighter", "targetId": "chars/goblin", "category": "Physical", "verb": "restraining" }
```

## Status Effects

A condition the engine didn't apply itself is a `status`; ending it is `status_remove`:
```json
{ "$type": "status", "characterId": "chars/wizard", "status": "Concentration" }
```

## Checklist

- [ ] Combat started, and the turn advanced with `combat(action: "next")`.
- [ ] Each action is a `ruleset_action`, with nothing the engine applies added by hand.
- [ ] A spell spent its slot in the same batch, and its components were possible.
- [ ] On a PC's turn I stopped and waited for the player.

## Feat effects (homebrew and SRD)
Feats can declare `effects` (seeded through `world_build` `feats[]`): fixed numeric modifiers to attack, damage, skill, save or AC rolls. The engine applies the number; you only supply facts.
- **Engine-checked** conditions (weapon `ranged`/`melee`/`finesse`/`twoHanded`/`heavy`) and player **toggles** (`parameters.powerAttack: "true"`) need nothing from you beyond the toggle.
- **DM-judged** conditions ("an ally is adjacent", "shooting from higher ground") are flags: put the ones you judge true right now in `parameters.assert` (comma-separated, e.g. `"allyNear,highGround"`). take_turn's `featChecklist` lists the flags every response while combat is active (everyone in round 1, the active character after). An effect whose flag you did not assert is *reported* in the roll result ("not applied: needs assert=…"), never applied silently. Never invent the number; pass only the flag.
- PF2e effects carry a `bonusType`; among a character's feat effects only the highest bonus and worst penalty of each typed kind count.
- A feat gated with `requires: {plugin, mode?}` is inert until that plugin is loaded (and that mode running, if named).
- A homebrew feat with no effects is flagged `feat_unimplemented_effects`; set `adjudicated: true` if you will apply it by judgment. Rules that fit no effect kind (reactions, triggers) belong in `mechanicalSummary` with `adjudicated: true`.
- Roll results echo every applied or unclaimed effect (attack, damage, skill, save, AC, PF2e grapple/escape). Not yet echoed: PF2e contested checks and spell saves against a target, and 5e/PF2e initiative (initiative effects are not supported).
