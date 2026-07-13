namespace CampaignVault.Tools;

/// <summary>
/// Canonical enum string reference for <c>commit</c> payloads — surfaced in tool description and get_help.
/// </summary>
internal static class CommitEnumCheatSheet
{
    internal const string Compact = """

**COMMIT ENUM VALUES (use these exact strings — case-sensitive):**
- `upsert_location.type` → Region, Settlement, District, Building, Room, Wilderness
  - Common mistakes: City/Town → **Settlement**; Tavern/Inn/Shop → **Building**
- `event.category` → Unresolved, Combat, Conversation, Discovery, Arrival, Betrayal, SceneCommit, Timeskip, Simulation, Interaction, Test, Travel, SceneInterrupt, Departure
  - Common mistake: Narrative/Roleplay → **Conversation**
  - **Conversation events MUST include `involved`: [`chars/pc`, `chars/npc`]** (every speaker). NOT `participants`.
- New rumor → use the `upsert_rumor` tool (`id`, `regionLocationId`, `subject`, `text`; starts Nascent)
- `rumor` (evolve, via commit) → `rumorId`, `newState`: Nascent, Spreading, Peak, Fading, Resolved, Forgotten
- `quest_progress.newState` / quest overall → Open, InProgress, Complete, Failed, Skipped
- `upsert_quest.urgency` → Low, Normal, Urgent, Critical
- `ruleset_action.actionType` → Attack, Spell, SkillCheck, ContestedCheck, OpposedCheck (alias), UseItem, Recovery, SavingThrow
- `ruleset_action.parameters.resolution` (Spell) → attack, save, check, utility, heal
- `ruleset_action.parameters.save` → 5e: Strength/Dexterity/Constitution/Intelligence/Wisdom/Charisma; PF2e: Fortitude/Reflex/Will
- `ruleset_action.parameters.halfOnSave` → true/false (5e save spells default true)
- `SavingThrow` = actor resists; `Spell`+`resolution:save` = all targetIds roll in ONE commit (NOT per-target SavingThrow)
- `SkillCheck` = non-magic skills; `Spell`+`resolution:check` = magic (Detect Magic). Engine auto-applies hp — no duplicate `hp` commits
- `ruleset_action.advantageState` → None, Advantage, Disadvantage
- `ruleset_action.actionCategory` → Melee, Ranged, Spell, Maneuver, Social, Survival
- `systemStats.classLevels` (5e multiclass) → [{ "class": "Fighter", "level": 5 }, { "class": "Wizard", "level": 5 }]
- `level_up.classGained` (5e multiclass) → e.g. "Wizard" — which class gained the level
- `knowledge_update.source` → Witnessed, Heard, Told, Experienced, Trauma, Conditioned
- `knowledge_update.valence` → Positive, Negative, Neutral, Traumatic
- `engagement_relation.category` → Physical, Social, Medical, Attention, Proximity
- `engagement_relation.restrictionLevel` → None, Soft, Hard
- `level_up.levelsGained` → positive integer (default 1); `hpMode` (5e) → average, rolled
- PCs: omit `maxHp` on create — use `systemStats.hitDie`/`level`/`constitution`; creatures use `statBlockHp` or `maxHp`
- Party currency → `$type: "resource"`, `poolName`: `gold` (dnd5e/pf2e) or `caps` (fallout2d20), `delta`: ±N

Full enum tables: call `get_help` → section **Commit Enum Values**.
""";

    internal const string Full = """

## Commit Enum Values (exact strings)

JSON enums in `commit` must match **exactly** (PascalCase as shown). Invalid values fail deserialization; the engine returns valid options and common-alias hints (e.g. City → Settlement).

### upsert_location / location_update
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `type` | Region, Settlement, District, Building, Room, Wilderness | City, Town → **Settlement**; Tavern, Inn, Shop → **Building** |

### event (`$type: event`)
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `category` | Unresolved, Combat, Conversation, Discovery, Arrival, Betrayal, SceneCommit, Timeskip, Simulation, Interaction, Test, Travel, SceneInterrupt, Departure | Narrative, Roleplay → **Conversation**; Scene → **Interaction** |
| `involved` | **Required** when `category` is `Conversation` | Array of character IDs for **every** participant (2+). Field name is `involved` (NOT `participants`). Auto-inferred/merged from `engagement_relation`, `spatial_position`, `activity`, `ruleset_action`, or other events in the same batch if omitted or partial. |

**Conversation commit template (copy-paste):**
{ "$type": "event", "category": "Conversation", "summary": "Valen asked Lirael about the missing caravans.", "involved": ["chars/valen", "chars/lirael-goldvein"] }

### upsert_rumor (creates or replaces a rumor — NOT a commit $type)
| Field | Notes |
|-------|--------|
| `id` | Required. e.g. `rumors/nightshade-gang` |
| `regionLocationId` | Required on create |
| `subject` | Short topic label |
| `text` (`currentText`) | Initial rumor body |

State always starts **Nascent** unless set explicitly.

### rumor (`$type: rumor` — evolve an EXISTING rumor via commit)
| Field | Valid values |
|-------|----------------|
| `rumorId` | Required — must already exist |
| `newState` | Nascent, Spreading, Peak, Fading, Resolved, Forgotten |
| `newText` | Optional updated text |

### upsert_quest / quest_progress
| Field | Valid values |
|-------|----------------|
| `urgency` (upsert_quest) | Low, Normal, Urgent, Critical |
| `newState` (progress) | Open, InProgress, Complete, Failed, Skipped |

Note: `upsert_quest.objectives[]` only needs `description` (+ optional `rewardHint`, `deadlineDay`). Objective state is advanced via `quest_progress.newState`.

### ruleset_action
| Field | Valid values |
|-------|----------------|
| `actionType` | Attack, Spell, SkillCheck, ContestedCheck, OpposedCheck (alias), UseItem, Recovery, SavingThrow |
| `actionCategory` | Melee, Ranged, Spell, Maneuver, Social, Survival |
| `advantageState` | None, Advantage, Disadvantage |
| `parameters.resolution` (Spell) | attack, save, check, utility, heal — **set explicitly** |
| `parameters.save` | 5e abilities; PF2e Fortitude/Reflex/Will; Fallout: use `saveAttribute` + optional `saveSkill` |
| `parameters.halfOnSave` | true/false — 5e defaults **true** (half damage on successful save) |
| `parameters.healDice` / `healBonus` / `healAmount` | Healing spells (5e/PF2e) or Stimpak-style items (Fallout flat `healAmount`) |
| `parameters.spellAttackBonus` / `dc` | Optional if caster has bootstrapped `spellAttackBonus` / `spellSaveDc` on systemStats |
| `parameters.targetPart` (Fallout) | Head, Neck, Torso, LeftArm, RightArm, LeftHand, RightHand, LeftLeg, RightLeg, LeftFoot, RightFoot |
| `parameters.bonusDice` / `useLuck` (Fallout) | Extra d20s in pool; `useLuck` adds +1 die (does not auto-spend luckPoints) |
| `parameters.rangeModifier` / `cover` (Fallout) | Added to attack difficulty (defense + modifiers) |
| `parameters.dc` (Fallout) | Alias for `difficulty` on saves/explosives |

### knowledge_update
| Field | Valid values |
|-------|----------------|
| `source` | Witnessed, Heard, Told, Experienced, Trauma, Conditioned |
| `valence` | Positive, Negative, Neutral, Traumatic |
| `urgency` | Low, Normal, High, Urgent |
| `importance` | Trivial, Important, Core |

### engagement_relation
| Field | Valid values |
|-------|----------------|
| `category` | Physical, Social, Medical, Attention, Proximity |
| `restrictionLevel` | None, Soft, Hard |

### upsert_faction (optional metadata)
| Field | Valid values |
|-------|----------------|
| `factionType` | Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, Criminal, Religious |

### upsert_item / item_update
| Field | Valid values |
|-------|----------------|
| `coreCategory` | Weapon, Armor, Clothing, Container, Consumable, Tool, Material, Valuable, Document, Key, Other |

### scene_interrupt_check (`$type: scene_interrupt_check`)
| Field | Notes |
|-------|--------|
| `locationId` | Required. Must have `ambientCrowd` or 3+ NPCs present. |
| `characterId` | Required. PC (or focal character) at that location. |
| `riskModifier` | Optional -50..+50. Like `encounterRiskModifier` on travel (+25 ≈ +12.5% chance). Auto-derived from `visualTags`/appearance if omitted. |
| `notes` | Optional flavor for the engine directive. |

Cooldown: one successful interrupt per location per in-game day. Do not use during active combat or on every dialog line.

### upsert_character.systemStats / system_stats
| Field | Valid values |
|-------|----------------|
| `$system` | dnd5e, pf2e, fallout2d20 (lowercase, exact — wrong casing silently falls back to untyped stats) |
| `hpMode` (5e) | average, rolled |
| `hitDie` (5e) | String on extension root — e.g. `"d12"` (NOT in `attributes`) |
| `level` | Integer on extension root (total character level) |
| `classLevels` (5e multiclass) | `[{ "class": "Fighter", "level": 5 }, { "class": "Wizard", "level": 5 }]` on systemStats |
| `spellcastingAbility` (5e) | Intelligence, Wisdom, or Charisma — derives spell DC/attack at bootstrap |
| `spellSaveDc` / `spellAttackBonus` (5e) | Optional overrides; omit to auto-derive from level + ability |
| `classHpPerLevel`, `ancestryHp` (PF2e) | Integers for HP derivation |
| `endurance`, `luck`, `hpPerLevel` (Fallout) | Integers for HP derivation |
| `statBlockHp` (all systems) | Authoritative creature HP; skips formula. PCs should omit. |

### level_up (`$type: level_up`)
| Field | Valid values / notes |
|-------|----------------------|
| `characterId` | Required character ID |
| `levelsGained` | Positive integer (default 1) |
| `hpMode` (5e) | average, rolled — override for this level gain |
| `healToMatch` | Boolean — if true, increase `currentHp` by the same amount as `maxHp` gain |
| `classGained` (5e multiclass) | Which class gained the level (e.g. `"Wizard"`) — sets hit die for HP gain |
| `reason` | Optional narrative milestone text logged in commit summary |
| Eligibility | `isPc: true` or `isPartyCompanion: true` — engine does not track XP; LLM commits when earned |

""";
}