---
name: dnd-combat
description: D&D combat initialization, turn order, actions, spells, HP, grapple, and status effects
metadata:
  type: skill
---

# Combat Mode

You are running a D&D combat encounter. These rules apply **only during active combat**.

## Core Rules

1. **Start combat** → `start_combat(campaignName)` → each combatant rolls initiative once
2. **Turn order** → `next_turn` advances turns, expires round-based status effects
3. **Every action resolves via `ruleset_action`** — never invent rolls yourself
4. **HP changes from `ruleset_action` only** — engine auto-applies, don't commit HP separately
5. **Grapple:** `ContestedCheck` + `Maneuver` in `ruleset_action`; engine handles engagement

## Action Types (ruleset_action.actionType)

- **Attack** — bonus, optional dc
- **SkillCheck** — skill name, dc
- **SavingThrow** — save type (Dexterity, etc.), dc
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

## Engagement & Spatial

After grapple success, engine auto-creates engagement. For manual engagement:
```json
{
  "$type": "engagement_relation",
  "characterId": "chars/fighter",
  "targetId": "chars/goblin",
  "category": "Physical",
  "verb": "grappling"
}
```

## Status Effects

Commit status changes:
```json
{
  "$type": "status",
  "characterId": "chars/wizard",
  "statusId": "Concentration",
  "newState": "active"
}
```

## Combat Checklist

- [ ] Did I call `start_combat` to initialize?
- [ ] Is this an action (attack/spell/move)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll result?
- [ ] Did time pass (turn advanced)? → `next_turn` or include in commit
- [ ] Did HP/status change? → Only via `ruleset_action` or dedicated `status`/`hp` commits
- [ ] Is someone grappling? → Include `engagement_relation` or let engine auto-create
