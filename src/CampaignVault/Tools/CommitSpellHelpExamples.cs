namespace CampaignVault.Tools;

/// <summary>
/// Copy-paste ruleset_action spell examples for get_help, commit hints, and system prompt.
/// </summary>
internal static class CommitSpellHelpExamples
{
    internal const string RoutingGuide = """
**Spell routing (`actionType: "Spell"` — use explicit `parameters.resolution`):**
- `attack` — spell attack vs AC (Fire Bolt). Omit `bonus` if caster has bootstrapped `spellAttackBonus`.
- `save` — ONE commit, ALL `targetIds`, caster sets `dc` + `save` + `damageDice`. **Targets roll saves.** Do NOT use per-target `SavingThrow` for AoE.
- `check` — non-combat roll: `dc` + `skill` (Detect Magic, Identify). `targetIds` optional.
- `utility` — non-combat with no roll; narrate outcome. Prefer `check` when a DC exists.
- `heal` — `healDice`/`healBonus`; `targetIds` optional (defaults to caster).

**SavingThrow vs Spell save:** `SavingThrow` = the **actor** resists one effect. `Spell` + `resolution: "save"` = **each target** resists the caster's spell in one commit.

**SkillCheck vs Spell+check:** Non-magic skill rolls (Athletics, Stealth, Perception) → `actionType: SkillCheck`. Magic → `actionType: Spell` + `parameters.resolution: check`.

**HP from ruleset_action:** The engine auto-applies `hp` deltas for attacks, saves, and heals. Do NOT also commit separate `hp` changes for the same hit.

**Spell slots:** commit `resource` with `spellName` (from `get_spells`) when spending `spell_slots_*` — engine validates slot level. Cantrips do not use slots.

**After any spell:** commit `status` separately for concentration, charm, etc.
""";

    internal const string ConcentrationStatus = """
[
  {
    "$type": "status",
    "characterId": "chars/wizard",
    "effect": {
      "name": "Concentration: Fireball",
      "category": "Condition",
      "recoveryHint": "Broken by damage (CON save) or casting another concentration spell."
    }
  }
]
""";

    internal const string FireballSave = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/wizard",
    "targetIds": ["chars/goblin-1", "chars/goblin-2", "chars/goblin-3"],
    "actionType": "Spell",
    "actionCategory": "Spell",
    "actionName": "Fireball",
    "damageType": "Fire",
    "parameters": {
      "resolution": "save",
      "dc": "15",
      "save": "Dexterity",
      "damageDice": "8d6",
      "halfOnSave": "true"
    }
  }
]
""";

    internal const string DetectMagicCheck = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/wizard",
    "actionType": "Spell",
    "actionCategory": "Spell",
    "actionName": "Detect Magic",
    "parameters": {
      "resolution": "check",
      "dc": "15",
      "skill": "Arcana"
    }
  }
]
""";

    internal const string HealingWord = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/cleric",
    "targetIds": ["chars/fighter"],
    "actionType": "Spell",
    "actionCategory": "Spell",
    "actionName": "Healing Word",
    "parameters": {
      "resolution": "heal",
      "healDice": "1d4",
      "healBonus": "3"
    }
  }
]
""";

    internal const string FireBoltAttack = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/wizard",
    "targetIds": ["chars/bandit"],
    "actionType": "Spell",
    "actionCategory": "Spell",
    "actionName": "Fire Bolt",
    "damageType": "Fire",
    "parameters": {
      "resolution": "attack",
      "damageDice": "1d10"
    }
  }
]
""";

    internal const string FalloutGrenade = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/raider",
    "targetIds": ["chars/pc1", "chars/pc2"],
    "actionType": "Spell",
    "actionCategory": "Spell",
    "actionName": "Frag Grenade",
    "parameters": {
      "resolution": "save",
      "dc": "2",
      "saveAttribute": "Endurance",
      "damageDice": "3"
    }
  }
]
""";

    internal const string FalloutStimpak = """
[
  {
    "$type": "ruleset_action",
    "characterId": "chars/pc1",
    "targetIds": ["chars/pc1"],
    "actionType": "UseItem",
    "actionName": "Stimpak",
    "parameters": { "healAmount": "8" }
  }
]
""";

    internal const string MulticlassBootstrap = """
Via world_build:
{ "characters": [ { "id": "chars/gish", "name": "Aldric", "isPc": true, "keepAlive": true,
  "classLevel": "Fighter 5 / Wizard 5",
  "systemStats": { "$system": "dnd5e", "constitution": 16, "intelligence": 16,
    "classLevels": [{ "class": "Fighter", "level": 5 }, { "class": "Wizard", "level": 5 }],
    "spellcastingAbility": "Intelligence",
    "skillModifiers": { "Arcana": 5, "Athletics": 7 } } } ] }
""";

    internal const string MulticlassLevelUp = """
{ "$type": "level_up", "characterId": "chars/gish", "levelsGained": 1, "classGained": "Wizard", "hpMode": "average" }
""";

    internal const string ResourceSpendExample = """
[
  {
    "$type": "resource",
    "characterId": "chars/wizard",
    "poolName": "spell_slots_3",
    "delta": -1,
    "spellName": "fireball",
    "reason": "Cast Fireball"
  }
]
""";

    internal const string RestRecoveryExample = """
[
  {
    "$type": "rest",
    "characterId": "chars/wizard",
    "locationId": "locations/inn_room",
    "intendedHours": 8,
    "securityModifier": 0
  }
]

This rest commit recovers eligible resource pools and settles tiredness immediately, per the rest type (LongRest ⊃ ShortRest ⊃ PerTurn hierarchy) — no separate advance_world call needed.
""";

    internal const string HelpSection = RoutingGuide + """

**Fireball (save — all targets, one commit):**
""" + FireballSave + """

**Detect Magic (non-combat check):**
""" + DetectMagicCheck + """

**Healing Word (heal):**
""" + HealingWord + """

**Fire Bolt (spell attack):**
""" + FireBoltAttack + """

**Fallout grenade (save):**
""" + FalloutGrenade + """

**Stimpak (UseItem):**
""" + FalloutStimpak + """

**Concentration (after save spell — separate commit):**
""" + ConcentrationStatus + """

**Resource spend (spell slots, ki, focus points — required for typed casters):**
""" + ResourceSpendExample + """

**Rest and recovery timing:**
""" + RestRecoveryExample;
}