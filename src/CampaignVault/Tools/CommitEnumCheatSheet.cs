namespace CampaignVault.Tools;

/// <summary>
/// Canonical enum string reference for <c>commit</c> payloads — surfaced in tool description and get_help.
/// </summary>
internal static class CommitEnumCheatSheet
{
    internal const string Full = """

## Commit Enum Values (exact strings)

JSON enums in `commit` must match **exactly** (PascalCase as shown). Invalid values fail deserialization; the engine returns valid options and common-alias hints (e.g. City → Settlement).

### world_build.locations[] / location_update
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `type` | Region, Settlement, District, Building, Room, Wilderness | City, Town → **Settlement**; Tavern, Inn, Shop → **Building** |

### event (`$type: event`)
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `category` | Unresolved, Combat, Conversation, Discovery, Arrival, Betrayal, SceneCommit, Timeskip, Simulation, Interaction, Test, Travel, SceneInterrupt, Departure | Narrative → **Conversation** |
| `involved` | **Req'd if Conversation** | Character IDs only. Use `locationId` for locations. Field is `involved` (NOT `participants`). |

### world_build.rumors[] (creates or replaces a rumor — NOT a commit $type)
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

### world_build.quests[] / quest_progress
| Field | Valid values |
|-------|----------------|
| `urgency` (world_build.quests[]) | Low, Normal, Urgent, Critical |
| `newState` (progress) | Open, InProgress, Complete, Failed, Skipped |

Note: `world_build.quests[].objectives[]` only needs `description` (+ optional `rewardHint`, `deadlineDay`). Objective state is advanced via `quest_progress.newState`.

### ruleset_action
| Field | Valid values |
|-------|----------------|
| `actionType` | Attack, Spell, SkillCheck, ContestedCheck, OpposedCheck (alias), UseItem, Recovery, SavingThrow |
| `actionCategory` | Melee, Ranged, Spell, Maneuver, Social, Survival |
| `advantageState` | None, Advantage, Disadvantage |
| `parameters.resolution` (Spell) | attack, save, check, utility, heal — **set explicitly** |
| `parameters.save` | 5e abilities; PF2e Fortitude/Reflex/Will |
| `parameters.halfOnSave` | true/false — 5e defaults **true** (half damage on successful save) |
| `parameters.healDice` / `healBonus` / `healAmount` | Healing spells (5e/PF2e) |
| `parameters.spellAttackBonus` / `dc` | Optional if caster has bootstrapped `spellAttackBonus` / `spellSaveDc` on systemStats |

### knowledge_update
| Field | Valid values |
|-------|----------------|
| `source` | Witnessed, Heard, Told, Experienced, Trauma, Conditioned |
| `valence` | Positive, Negative, Neutral, Traumatic |
| `urgency` | Low, Normal, High, Urgent |
| `importance` | Trivial, Important, Core |
| `sourceEventIds` | **Required if `source` is Witnessed or Experienced.** Pass a client-chosen `eventId` on the paired event change in the same batch and reference it here — batch fails validation and rolls back entirely without it. |

### engagement_relation
| Field | Valid values |
|-------|----------------|
| `category` | Physical, Social, Medical, Attention, Proximity |
| `restrictionLevel` | None, Soft, Hard |

### world_build.factions[] (optional metadata)
| Field | Valid values |
|-------|----------------|
| `factionType` | Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, Criminal, Religious |

### world_build.items[] / item_update
| Field | Valid values |
|-------|----------------|
| `coreCategory` | Weapon, Armor, Clothing, Container, Consumable, Tool, Material, Valuable, Document, Key, Other |
| `equipZones` (equip path) | Head, Face, Neck, Torso, Back, Waist, Hands, Wrists, Legs, Feet, MainHand, OffHand, Ring, Accessory |
| `equipLayer` (equip path) | Base, Armor, Outer, Held |

### world_build.locations[] / location_update (climate)
| Field | Valid values |
|-------|----------------|
| `climateZone` | Arctic, Tundra, Temperate, Desert, Tropical, Alpine, Subterranean |

### scene_interrupt_check (`$type: scene_interrupt_check`)
| Field | Notes |
|-------|--------|
| `locationId` | Required. Must have `ambientCrowd` or 3+ NPCs present. |
| `characterId` | Required. PC (or focal character) at that location. |
| `riskModifier` | Optional -50..+50. Like `encounterRiskModifier` on travel (+25 ≈ +12.5% chance). Auto-derived from `visualTags`/appearance if omitted. |
| `notes` | Optional flavor for the engine directive. |

Cooldown: one successful interrupt per location per in-game day. Do not use during active combat or on every dialog line.

### world_build.characters[].systemStats / character_update.systemStats
| Field | Valid values |
|-------|----------------|
| `$system` | dnd5e, pf2e (lowercase, exact — wrong casing silently falls back to untyped stats) |
| `hpMode` (5e) | average, rolled |
| `hitDie` (5e) | String on extension root — e.g. `"d12"` (NOT in `attributes`) |
| `level` | Integer on extension root (total character level) |
| `classLevels` (5e multiclass) | `[{ "class": "Fighter", "level": 5 }, { "class": "Wizard", "level": 5 }]` on systemStats |
| `spellcastingAbility` (5e) | Intelligence, Wisdom, or Charisma — derives spell DC/attack at bootstrap |
| `spellSaveDc` / `spellAttackBonus` (5e) | Optional overrides; omit to auto-derive from level + ability |
| `classHpPerLevel`, `ancestryHp` (PF2e) | Integers for HP derivation |
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
| `choices` | `{ key: chosenOptionId }` from `get_rules_reference kind:'level_up'` (e.g. `subclass`, `fightingStyle`, `asiOrFeat`) — appended to the character's level-up choice history, never overwrites earlier picks |
| `abilityScoreIncreases` (5e only) | `{ Ability: amount }` for an ASI, e.g. `{ "Strength": 2 }` or `{ "Strength": 1, "Dexterity": 1 }` |
| Eligibility | `isPc: true` or `isPartyCompanion: true` — milestone campaigns level on narrative say-so; XP campaigns surface an XP_THRESHOLD pressure once `xp_grant`-tracked XP crosses the threshold |

""";
}