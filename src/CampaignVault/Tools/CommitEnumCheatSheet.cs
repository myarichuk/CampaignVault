namespace CampaignVault.Tools;

/// <summary>
/// Canonical enum string reference for <c>commit</c> payloads — surfaced in tool description and get_help.
/// </summary>
internal static class CommitEnumCheatSheet
{
    internal const string Compact = """

**COMMIT ENUM VALUES (use these exact strings — case-sensitive):**
- `location_create.type` → Region, Settlement, District, Building, Room, Wilderness
  - Common mistakes: City/Town → **Settlement**; Tavern/Inn/Shop → **Building**
- `event.category` → Unresolved, Combat, Conversation, Discovery, Arrival, Betrayal, SceneCommit, Timeskip, Simulation, Interaction, Test, Travel, SceneInterrupt
  - Common mistake: Narrative/Roleplay → **Conversation**
  - **Conversation events MUST include `involved`: [`chars/pc`, `chars/npc`]** (every speaker). NOT `participants`.
- `rumor.newState` → Nascent, Spreading, Peak, Fading, Resolved, Forgotten
- `quest_progress.newState` / quest overall → Open, InProgress, Complete, Failed, Skipped
- `quest_create.urgency` → Low, Normal, Urgent, Critical
- `ruleset_action.actionType` → Attack, Spell, SkillCheck, ContestedCheck, OpposedCheck, UseItem, Recovery, SavingThrow, Meta
- `ruleset_action.advantageState` → None, Advantage, Disadvantage
- `ruleset_action.actionCategory` → Melee, Ranged, Spell, Maneuver, Social, Survival
- `knowledge_update.source` → Witnessed, Heard, Told, Experienced, Trauma, Conditioned
- `knowledge_update.valence` → Positive, Negative, Neutral, Traumatic
- `engagement_relation.category` → Physical, Social, Medical, Attention, Proximity
- `engagement_relation.restrictionLevel` → None, Soft, Hard

Full enum tables: call `get_help` → section **Commit Enum Values**.
""";

    internal const string Full = """

## Commit Enum Values (exact strings)

JSON enums in `commit` must match **exactly** (PascalCase as shown). Invalid values fail deserialization; the engine returns valid options and common-alias hints (e.g. City → Settlement).

### location_create / location_update
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `type` | Region, Settlement, District, Building, Room, Wilderness | City, Town → **Settlement**; Tavern, Inn, Shop → **Building** |

### event (`$type: event`)
| Field | Valid values | LLM alias hints |
|-------|----------------|-----------------|
| `category` | Unresolved, Combat, Conversation, Discovery, Arrival, Betrayal, SceneCommit, Timeskip, Simulation, Interaction, Test, Travel, SceneInterrupt | Narrative, Roleplay → **Conversation**; Scene → **Interaction** |
| `involved` | **Required** when `category` is `Conversation` | Array of character IDs for every participant. Field name is `involved` (NOT `participants`). Auto-inferred from `engagement_relation`/`activity` in the same batch if omitted. |

**Conversation commit template (copy-paste):**
{ "$type": "event", "category": "Conversation", "summary": "Valen asked Lirael about the missing caravans.", "involved": ["chars/valen", "chars/lirael-goldvein"] }

### rumor (`$type: rumor`)
| Field | Valid values |
|-------|----------------|
| `newState` | Nascent, Spreading, Peak, Fading, Resolved, Forgotten |

### quest_create / quest_progress
| Field | Valid values |
|-------|----------------|
| `urgency` (create) | Low, Normal, Urgent, Critical |
| `newState` (progress) | Open, InProgress, Complete, Failed, Skipped |
| `overallState` (create) | Open, InProgress, Complete, Failed, Skipped |

Note: `quest_create.objectives[]` only needs `description` (+ optional `rewardHint`, `deadlineDay`). Objective state is advanced via `quest_progress.newState`.

### ruleset_action
| Field | Valid values |
|-------|----------------|
| `actionType` | Attack, Spell, SkillCheck, ContestedCheck, OpposedCheck, UseItem, Recovery, SavingThrow, Meta |
| `actionCategory` | Melee, Ranged, Spell, Maneuver, Social, Survival |
| `advantageState` | None, Advantage, Disadvantage |
| `parameters.targetPart` (Fallout) | Head, Neck, Torso, LeftArm, RightArm, LeftHand, RightHand, LeftLeg, RightLeg, LeftFoot, RightFoot |

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

### faction_create (optional metadata)
| Field | Valid values |
|-------|----------------|
| `type` | Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, Criminal, Religious |
| `stance` | Neutral, Allied, TradePartner, Hostile, AtWar, Subjugated, Opportunistic |

### item_create / item_update
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

### character_create.systemStats
| Field | Valid values |
|-------|----------------|
| `$system` | Dnd5e, Pathfinder2e, Fallout2d20 |

""";
}