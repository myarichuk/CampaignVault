---
name: dnd5e-combat
description: D&D 5e combat specifics — attack flags (off-hand, Extra Attack, Cunning Action, Action Surge), spell examples and resolution, slots and components, concentration, death saves and rest. Load with dnd-combat (the shared flow); Pathfinder 2e campaigns use pf2e-combat instead.
metadata:
  type: skill
---

# Combat (D&D 5e)

Applies only when the campaign's ruleset is `dnd5e`. The flow, action types, restraint, status effects, range and feat effects are in `dnd-combat`; this skill is what 5e adds.

## Attack flags

The derived attack bonus is ability + proficiency + fighting style + weapon enchantment. Optional per-attack flags: `powerAttack` (only if a recorded feat defines that toggle), `sneakAttack` (ally adjacent), `offHand` (bonus-action off-hand attack; no ability mod on damage without Two-Weapon Fighting; refused before an Attack action this turn, and, when weapons are equipped in the hand zones, unless two light weapons are wielded — `item_equip` both), `actionSurge`, `bonusAction`, `assert` (see below). List one target per swing of Extra Attack; the result warns if `targetIds` outnumber the attacks one action allows or `attackCount` skips a listed target. Cunning Action is its own bonus action: `actionName` "Cunning Action", `parameters.option` dash|disengage|hide (hide needs `dc`). Bard Jack of All Trades is applied automatically to unproficient ability checks and initiative. Extra Attack (Fighter 5+) needs no flag: the further swings of one Attack action don't cost another action. Record fighting style/subclass via level_up choices or `systemStats.levelUpChoices`, or Archery/Dueling won't apply.

## Spell Examples

**Fire Bolt** (attack; no `bonus`, the sheet supplies it):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/wizard",
  "targetIds": ["chars/goblin"],
  "actionType": "Spell",
  "actionName": "Fire Bolt",
  "parameters": { "resolution": "attack", "damageDice": "1d10" }
}
```

**Magic Missile** (auto-hit: `resolution: "attack"`, and the engine skips the roll because the spell never misses; `slotLevel` sets the dart count, one more per slot above 1st; darts split across the listed targets):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/mage",
  "targetIds": ["chars/goblin-1"],
  "actionType": "Spell",
  "actionName": "Magic Missile",
  "parameters": { "resolution": "attack", "slotLevel": 3 }
}
```
Leave out `damageDice` for a handbook spell and the engine uses the spell's own dice for the slot (or for the caster's level, for a cantrip).

**Cantrip damage scales with character level, not spell level:** 1 die at levels 1–4, 2 at 5–10, 3 at 11–16, 4 at 17–20, and no ability modifier on the damage. Every SRD spell carries its damage and save data, so the engine warns when the dice or save you send disagree with the spell; the damage still applies as sent, and homebrew spells cast normally.

**Fireball** (save, all targets; omit `dc` and the caster's spell save DC is used):
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/wizard",
  "targetIds": ["chars/goblin-1", "chars/goblin-2", "chars/goblin-3"],
  "actionType": "Spell",
  "actionName": "Fireball",
  "parameters": { "resolution": "save", "save": "Dexterity", "damageDice": "8d6" }
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

Casting also spends the slot — commit `{ "$type": "resource", "characterId": "chars/wizard", "poolName": "spell_slots_3", "delta": -1 }` in the same batch. A leveled cast with no slot spend in the batch is refused, as is a spell that isn't on the caster's cantrip, prepared or known list, and overspend hard-fails; narrate the fizzle and let the player pick another. Rituals, innate or racial spells, focus spells and scroll or wand casts skip both checks: set `parameters.ritual`, `innate`, `focus` or `item` to `true`.

A 5e concentration spell puts the Concentration status on its caster itself; don't commit one by hand.

## Down, dying and rest

- At 0 HP a PC gets Unconscious and Prone from the engine, and the turn-start notice says a death save is due: commit `{ "$type": "death_save", "characterId": "..." }`. Spare the Dying or a passed Medicine DC 10 check is `{ "$type": "death_save", "characterId": "...", "stabilize": true }` (stable at 0 HP, tally reset). Nothing rolls on its own.
- Temporary HP comes from `grant_temp_hp`, not a heal; the larger value wins and it fades on a long rest.
- A long rest restores HP and hit dice, and lowers exhaustion by one level when the character has eaten and drunk. Don't hand-commit `hp` for it.

## Checklist

- [ ] A spell spent its slot in the same batch, and its components were possible.
- [ ] A leveled spell is on the caster's list; a cantrip's dice match the character level.
- [ ] Nothing the engine applies (damage, conditions, concentration, the long rest's HP) was added by hand.
