---
name: dnd-combat
description: D&D combat initialization, turn order, actions, spells, HP, grapple, and status effects
metadata:
  type: skill
---

# Combat Mode

You are running a D&D combat encounter. These rules apply **only during active combat**.

## Core Rules

1. **Start combat** → `combat(action: "start", locationId, combatantIds)` → each combatant rolls initiative once
2. **Turn order** → `combat(action: "next")` advances turns, expires round-based status effects
3. **Every action resolves via `ruleset_action`** — never invent rolls yourself
4. **HP changes from `ruleset_action` only** — engine auto-applies, don't commit HP separately
5. **Grapple:** `ContestedCheck` + `Maneuver` in `ruleset_action`; engine handles engagement
6. **A PC's turn is a hard stop, not a beat to narrate through.** When `combat(action: "next")` lands on a PC, describe the round state (what changed, who's threatening whom, any status ticking) and stop — wait for the player's stated action before committing any `ruleset_action` for that PC. Never pick their target, action, or spell for them, and never chain a PC's turn straight into the next NPC's without their input in between. An NPC combatant's turn is the one case you resolve and narrate in the same batch — their action comes from Psychology/TurnIntent, not the player.
7. **Don't refuse a legal combat action outright.** If a PC declares an attack/maneuver the rules and fiction support, resolve it via the matching `ruleset_action` and let the roll decide — a low chance to hit is not a reason to refuse the attempt.

Only bundle multiple actors into one `take_turn` when they're genuinely simultaneous (e.g., an AoE hitting several targets in the same instant) — see `dnd-bundling`'s "intervening player decision/round" rule for the general cross-skill version of this.

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

## Spell Components

Before resolving a `Spell` action, check whether the caster can actually supply what the spell requires:
- **Verbal** — can the caster speak? Gagged, silenced, or similar conditions block this.
- **Somatic** — does the caster have a free hand/gesture available? Bound or fully-occupied hands block this (unless a feat like War Caster waives it).
- **Material** — does the caster possess the required component/focus, or is it a costly component they're carrying?

The engine enforces this automatically for known SRD/homebrew spells against the caster's active status effects — you don't need to hand-roll the check. But when *you* apply a status effect (standard or homebrew — Gagged, Bound, mind control, a magical silence zone, etc.) that should block a component, tag it so the engine actually catches it on later turns instead of just this one narration: set `BlocksVerbalComponents`, `BlocksSomaticComponents`, `BlocksMaterialComponents`, or `BlocksAllActions` (any nonzero value) in that status effect's `statModifiers`. A feat or temporary effect that lifts a block (e.g. Subtle Spell) uses the matching `WaivesVerbalComponents`/`WaivesSomaticComponents`/`WaivesMaterialComponents` key instead. If the engine rejects a cast with `[SpellcastingBlocked]`, that's a legitimate mechanical outcome — narrate why, don't retry around it.

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

- [ ] Did I call `combat(action: "start")` to initialize?
- [ ] Is this an action (attack/spell/move)? → `ruleset_action` first
- [ ] Did I narrate sensory outcome from the roll result?
- [ ] Did time pass (turn advanced)? → `combat(action: "next")` or `minutesElapsed` on the take_turn request
- [ ] Did HP/status change? → Only via `ruleset_action` or dedicated `status`/`hp` commits
- [ ] Is someone grappling? → Include `engagement_relation` or let engine auto-create
- [ ] If it just advanced to a PC's turn, did I stop instead of choosing their action for them?
- [ ] Did I resolve the PC's declared action via a check instead of refusing it outright?
