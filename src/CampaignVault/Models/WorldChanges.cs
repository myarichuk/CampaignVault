using System.ComponentModel;
using System.Text.Json.Serialization;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Models;

/// <summary>
/// Base for all atomic, composable world mutations that can be sent to <c>commit</c>.
/// The LLM must include the exact <c>$type</c> discriminator so the server knows which concrete change to apply.
/// Mix as many different change kinds as needed in a single call for atomicity.
/// </summary>
[Description("Base for all atomic world mutations sent via the 'commit' tool. Every item must include the exact $type discriminator. Mix freely (hp + activity + relationship + need + event, etc.).")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HpChange), "hp")]
[JsonDerivedType(typeof(ItemTransfer), "item")]
[JsonDerivedType(typeof(StatusChange), "status")]
[JsonDerivedType(typeof(StatusRemove), "statusremove")]
[JsonDerivedType(typeof(EventOccurred), "event")]
[JsonDerivedType(typeof(RumorEvolves), "rumor")]
[JsonDerivedType(typeof(RumorCreate), "rumor_create")]
[JsonDerivedType(typeof(RelationshipChange), "relationship")]
[JsonDerivedType(typeof(NeedChange), "need")]
[JsonDerivedType(typeof(AttributeChange), "attribute")]
[JsonDerivedType(typeof(MoodChange), "mood")]
[JsonDerivedType(typeof(ActivityChange), "activity")]
[JsonDerivedType(typeof(RulesetAction), "ruleset_action")]
[JsonDerivedType(typeof(LocationCreate), "location_create")]
[JsonDerivedType(typeof(LocationUpdate), "location_update")]
[JsonDerivedType(typeof(CharacterCreate), "character_create")]
[JsonDerivedType(typeof(LevelUpChange), "level_up")]
[JsonDerivedType(typeof(ScheduleChange), "schedule_change")]
[JsonDerivedType(typeof(ItemCreate), "item_create")]
[JsonDerivedType(typeof(TravelChange), "travel")]
[JsonDerivedType(typeof(FactionReputationChange), "faction_reputation")]
[JsonDerivedType(typeof(FactionStateChange), "faction_state")]
[JsonDerivedType(typeof(QuestCreate), "quest_create")]
[JsonDerivedType(typeof(QuestProgress), "quest_progress")]
[JsonDerivedType(typeof(FactionCreate), "faction_create")]
[JsonDerivedType(typeof(RestChange), "rest")]
[JsonDerivedType(typeof(ItemUpdate), "item_update")]
[JsonDerivedType(typeof(CharacterUpdate), "character_update")]
[JsonDerivedType(typeof(SystemStatsChange), "system_stats")]
[JsonDerivedType(typeof(KnowledgeUpdate), "knowledge_update")]
[JsonDerivedType(typeof(EngagementRelationChange), "engagement_relation")]
[JsonDerivedType(typeof(SpatialPositionChange), "spatial_position")]
[JsonDerivedType(typeof(SceneInterruptCheck), "scene_interrupt_check")]
public abstract class WorldChange;

/// <summary>
/// Attempt to pass time resting. The engine calculates the danger of the location and the LLM's security modifier
/// to determine if the rest is interrupted by an encounter.
/// </summary>
public class RestChange : WorldChange
{
    [Description("ID of the character attempting to rest.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("ID of the location where they are resting.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = default!;

    [Description("How many hours the character intends to rest. (e.g., 1 for short, 8 for long).")]
    [JsonPropertyName("intendedHours")]
    public int IntendedHours { get; set; }

    [Description("Modifier representing the safety of the setup (-50 to +50). E.g., +20 for stealthy hidden camp, +100 for Tiny Hut, -20 for drunk in an alley.")]
    [JsonPropertyName("securityModifier")]
    public int SecurityModifier { get; set; }

    [Description("Narrative description of how the character rests.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }
}

/// <summary>
/// Optional single-roll check for whether someone from the ambient crowd interrupts the scene.
/// LLM calls this after tense beats in crowded locations — not on every line of dialog.
/// </summary>
public class SceneInterruptCheck : WorldChange
{
    [Description("ID of the location where the scene beat occurred.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = default!;

    [Description("ID of the character whose vulnerability/flavor drives the check (usually the PC).")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Abstract modifier from -50 to +50 representing crowd reaction risk, like encounterRiskModifier on travel. Positive = PC looks vulnerable/provocative (bloodied, wanted, insulted a guard). Negative = PC looks safe (well_armed, escorted). If omitted, engine auto-derives from visualTags/appearance/equipment.")]
    [JsonPropertyName("riskModifier")]
    public int? RiskModifier { get; set; }

    [Description("Optional free-text flavor for the engine directive (e.g. 'Bloodied wanted face, Schlag drawn, crowd already hostile').")]
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>Adjust a character's current HP by a delta. Positive heals, negative damages.</summary>
public class HpChange : WorldChange
{
    [Description("ID of the character whose HP to modify (e.g. 'characters/grog' or 'characters/elara-voss'). Must exist.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Delta to apply to CurrentHp. Positive = heal/gain, negative = damage/loss. Use small values for normal hits, larger for big effects.")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }
}

/// <summary>Move/transfer an existing item to a new holder (character, location, or another container item).</summary>
public class ItemTransfer : WorldChange
{
    [Description("ID of the item being moved (e.g. 'items/iron-key-17').")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = default!;

    [Description("New holder ID. Can be a character ('characters/xxx'), a location ('locations/xxx'), or another item acting as container.")]
    [JsonPropertyName("toHolderId")]
    public string ToHolderId { get; set; } = default!;
}

/// <summary>
/// Add a structured status effect to a character.
/// Prefer supplying <see cref="Effect"/> for full control over modifiers and expiration.
/// The legacy <see cref="Status"/> string field is accepted for backward compatibility
/// and creates a minimal effect with no modifiers and no expiration.
///
/// The LLM DM is the sole author of the effect's stat modifiers, expiration, and recovery hint.
/// The MCP stores the effect and auto-expires it when ExpiresAtDay or ExpiresAtRound is reached.
/// </summary>
public class StatusChange : WorldChange
{
    [Description("ID of the character receiving the status (e.g. 'characters/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description(
        "[Preferred] Fully structured StatusEffect authored by the LLM DM. " +
        "Provide name, category, optional affectedPart (BodyPart enum), statModifiers (key-value penalties/bonuses), " +
        "and optionally expiresAtDay (CampaignTime.TotalDaysElapsed + N) or expiresAtRound (CombatEncounter.Round + N). " +
        "Leave both expiration fields null for permanent effects (broken bones, curses). " +
        "Set recoveryHint to a free-text note for your own future reference about how this effect can be removed.")]
    [JsonPropertyName("effect")]
    public StatusEffect? Effect { get; set; }

    [Description(
        "[Legacy fallback] Plain condition name string (e.g. 'Poisoned', 'Frightened', 'OnFire'). " +
        "Use this only when you do not need stat modifiers or expiration tracking. " +
        "Prefer the 'effect' field for all new usage.")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Remove a named status/condition from a character (case-insensitive match). Removes all matching entries.</summary>
public class StatusRemove : WorldChange
{
    [Description("ID of the character whose status to remove (e.g. 'characters/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Name of the status condition to remove. Matching is case-insensitive; all matching entries are removed.")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = default!;
}

/// <summary>
/// Record a noteworthy occurrence in the world. Use Category='Unresolved' for open plot threads the party should care about.
/// These appear in get_scene, recall_history, and get_world_state.
/// </summary>
public class EventOccurred : WorldChange
{
    [Description("Short human-readable summary of what happened. This becomes the main text of the event log entry.")]
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = default!;

    [Description("Classification of the event. Use 'Unresolved' for dangling plot hooks the party should follow up on. Other good values: 'Combat', 'Conversation', 'Discovery', 'Arrival', 'Betrayal', 'SceneInterrupt' (engine-emitted crowd interrupts).")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventCategory Category { get; set; } = EventCategory.Unresolved;

    [Description("Character IDs of everyone who participated. REQUIRED when category is 'Conversation' — without this, get_npc_context cannot recall the exchange. Use exact IDs (e.g. ['chars/valen', 'chars/lirael-goldvein']). Field name is 'involved' (NOT 'participants'). If omitted but engagement_relation/activity for the same speakers are in the same commit batch, the engine auto-infers.")]
    [JsonPropertyName("involved")]
    public List<string>? Involved { get; set; }

    [Description("Optional emotional beat for relational initiative, e.g. 'gratitude', 'affection', 'betrayal', 'gift_received'.")]
    [JsonPropertyName("emotionalBeat")]
    public string? EmotionalBeat { get; set; }

    [Description("Optional. Item, character, or location ID this beat relates to.")]
    [JsonPropertyName("relatedEntityId")]
    public string? RelatedEntityId { get; set; }
}

/// <summary>Change the state of an existing rumor and optionally rewrite its current text.</summary>
public class RumorEvolves : WorldChange
{
    [Description("ID of the rumor to evolve (e.g. 'rumors/bandit-activity-in-woods'). Must already exist.")]
    [JsonPropertyName("rumorId")]
    public string RumorId { get; set; } = default!;

    [Description("New lifecycle state for the rumor. One of: Nascent, Spreading, Peak, Fading, Resolved, Forgotten. Use Resolved or Forgotten to retire a rumor.")]
    [JsonPropertyName("newState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RumorState NewState { get; set; }

    [Description("Optional new narrative text for the rumor. If omitted the previous text is kept.")]
    [JsonPropertyName("newText")]
    public string? NewText { get; set; }
}

/// <summary>Seed a new rumor. State starts at Nascent; advance via <see cref="RumorEvolves"/> ($type rumor).</summary>
public class RumorCreate : WorldChange
{
    [Description("Unique rumor ID (e.g. 'rumors/nightshade-gang'). Namespace with campaign slug when campaign-specific.")]
    [JsonPropertyName("rumorId")]
    public string RumorId { get; set; } = default!;

    [Description("Short topic label (e.g. 'Nightshade Gang').")]
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = default!;

    [Description("Initial rumor text heard by the party or world.")]
    [JsonPropertyName("text")]
    public string Text { get; set; } = default!;

    [Description("Optional location IDs this rumor is tied to. First ID becomes RegionLocationId; omit for global.")]
    [JsonPropertyName("relatedLocationIds")]
    public List<string>? RelatedLocationIds { get; set; }
}

/// <summary>Apply a numeric delta to the relationship score between two characters. Range is typically -100 to +100.</summary>
public class RelationshipChange : WorldChange
{
    [Description("ID of the character whose opinion of the target is changing (e.g. 'characters/elara-voss').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("ID of the target character being evaluated (e.g. 'characters/bram-ironarm').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = default!;

    [Description("Numeric delta to apply to the relationship score. Positive = better opinion/trust, negative = worse. Typical range -20 to +20 per significant event.")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }

    [Description("Narrative reason for the shift. This is stored with the relationship and helps the behavioral synthesizer explain why the NPC feels this way.")]
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = default!;
}

/// <summary>
/// Establish, update, or remove a pairwise engagement state between two entities (grapple, embrace, watch, etc.).
/// For zone/distance positioning, use a future spatial-position change instead.
/// </summary>
public class EngagementRelationChange : WorldChange
{
    [Description("ID of the character initiating or anchoring the relation (e.g. 'characters/bard').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("ID of the target character or object (e.g. 'characters/archivist').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = default!;

    [Description("Engagement category: Physical, Social, Medical, Attention, or Proximity.")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementCategory? Category { get; set; }

    [Description("Freeform verb (e.g. 'grappling', 'ranting at', 'stitching'). Use null/empty with relationType null to remove.")]
    [JsonPropertyName("verb")]
    public string? Verb { get; set; }

    [Description("Optional restriction override: None, Soft, or Hard.")]
    [JsonPropertyName("restrictionLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementRestrictionLevel? RestrictionLevel { get; set; }

    [Description("Whether to automatically establish the inverse relationship on the target (e.g., if actor Grapples target, target becomes GrappledBy actor).")]
    [JsonPropertyName("bidirectional")]
    public bool Bidirectional { get; set; } = true;
}

/// <summary>
/// Establish, update, or remove relative zone/distance positioning for a character.
/// For pairwise grapple/embrace anchors, use <see cref="EngagementRelationChange"/> instead.
/// </summary>
public class SpatialPositionChange : WorldChange
{
    [Description("ID of the character whose position is being set (e.g. 'characters/drunk').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("ID of the reference entity (e.g. 'characters/pc', 'locations/tavern_bar').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = default!;

    [Description("Distance band (Touch, Close, Near, Far, Distant). Use null or empty to remove.")]
    [JsonPropertyName("distanceBand")]
    public string? DistanceBand { get; set; }

    [Description("Optional bearing (North, Behind, AtBar, etc.).")]
    [JsonPropertyName("bearing")]
    public string? Bearing { get; set; }

    [Description("Optional sub-zone within the scene (bar, doorway, etc.).")]
    [JsonPropertyName("zone")]
    public string? Zone { get; set; }
}

/// <summary>Adjust one of a character's open-ended psychological or physical needs (hunger, thirst, tiredness, wanderlust, duty, etc.).</summary>
public class NeedChange : WorldChange
{
    [Description("ID of the character whose need is changing (e.g. 'characters/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Name of the need being adjusted. The system is intentionally open: use 'hunger', 'thirst', 'tiredness', 'social_drive', or invent narrative-appropriate ones like 'wanderlust', 'homesickness', 'vengeance', 'duty'.")]
    [JsonPropertyName("need")]
    public string Need { get; set; } = default!;

    [Description("Delta to apply. Negative values satisfy/reduce the need (e.g. feeding someone). Positive values increase the drive (e.g. marching all day raises tiredness).")]
    [JsonPropertyName("delta")]
    public float Delta { get; set; }
}

/// <summary>Set or delta an arbitrary narrative attribute on a character (willpower, temperature, morale, corruption, reputation, etc.).</summary>
public class AttributeChange : WorldChange
{
    [Description("ID of the character receiving the attribute change.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Name of the attribute. Common examples: 'willpower', 'temperature', 'morale', 'corruption', 'reputation', 'fear'. Invent others that fit the story.")]
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = default!;

    [Description("The new absolute value for the attribute, unless IsDelta is true.")]
    [JsonPropertyName("value")]
    public float Value { get; set; }

    [Description("If true, Value is treated as a delta to be added to the current attribute value instead of an absolute override.")]
    [JsonPropertyName("isDelta")]
    public bool IsDelta { get; set; }
}

/// <summary>
/// Directly override an NPC's CurrentMood (short emotional state string shown in get_scene).
/// Prefer using simulation rules + MoodChange deltas when possible; this is for strong narrative moments.
/// </summary>
public class MoodChange : WorldChange
{
    [Description("ID of the character whose mood to set.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("New short mood string (e.g. 'grimly determined', 'euphoric', 'terrified', 'playfully drunk', 'brooding'). Keep it evocative but concise.")]
    [JsonPropertyName("newMood")]
    public string NewMood { get; set; } = default!;
}

/// <summary>
/// Force-update what an NPC is currently doing and/or where they are physically located.
/// This immediately affects what get_scene returns for that NPC's CurrentActivity and CurrentLocationId.
/// Use liberally at the end of roleplay or combat so the world model stays in sync with the story.
/// </summary>
public class ActivityChange : WorldChange
{
    [Description("ID of the character whose activity/location is changing (e.g. 'characters/bram-ironarm').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("What the character is now visibly doing (e.g. 'tending bar and watching the door', 'sleeping in the corner', 'arguing with the blacksmith', 'on patrol at the old watchtower'). Omit to leave activity unchanged.")]
    [JsonPropertyName("newActivity")]
    public string? NewActivity { get; set; }

    [Description("New location ID for the character (e.g. 'locations/rusty-nail'). This must be a valid location. Omit the key (or leave null without updateLocation:true) to leave location unchanged. Pass null + updateLocation:true to clear the character's position (e.g. transient eviction).")]
    [JsonPropertyName("newLocationId")]
    public string? NewLocationId { get; set; }

    [Description("Set to true when supplying newLocationId (including null to clear). This distinguishes an explicit location update/clear from an omitted newLocationId key in the JSON (which means 'leave location unchanged'). Internal simulation rules set this when emitting moves or clears.")]
    [JsonPropertyName("updateLocation")]
    public bool UpdateLocation { get; set; }

    [Description("Optional narrative justification for the change. Stored for later behavioral synthesis and debugging.")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Trigger a ruleset-specific action (attack, skill check, contested roll, recovery, etc.).
/// The active IRulesetResolver (selected from CampaignConfig.ActiveSystem) handles the math
/// and returns primitive WorldChange mutations (HpChange, StatusChange, NeedChange, etc.)
/// back into the StageChangesAsync pipeline.
///
/// IMPORTANT: The LLM must NOT invent random numbers. Use this action type and let the
/// C# resolver roll dice deterministically. The LLM receives back a structured RollResult
/// and narrates the outcome.
///
/// parameters keys (all optional, resolver-specific; values may be strings or numbers in JSON):
///   "dc"            – difficulty class for skill/opposed checks
///   "bonus"         – attack roll bonus for D&amp;D 5e attacks (alias: "toHitBonus")
///   "damageDice"    – damage expression for attacks (e.g. "1d8")
///   "damageBonus"   – flat damage bonus for attacks
///   "ac"            – target AC override for attacks
///   "difficulty"    – success count threshold for Fallout 2d20 (default "1")
///   "targetPart"    – BodyPart enum string for hit-location targeting (Fallout 2d20)
///   "advantage"     – "true"/"false" for D&amp;D 5e
///   "item"          – item document ID for UseItem actions (alias: weaponItemId, weapon)
///   "weaponItemId"  – held weapon item ID; properties merge into attack if parameters omitted
///   "attackCount"   – max separate attack rolls (multi-target / repeating weapons); defaults to targetIds.Count when multiple targets listed
///   "initiativeSkill" – skill name overriding default initiative (PF2e)
///   "targetSkill"   – skill name for the target side of a ContestedCheck
///   "resolution"    – spell routing: attack, save, check, utility, heal (alias: spellResolution)
///   "save"          – saving throw ability for save-based spells (5e/PF2e) or saveAttribute (Fallout)
///   "halfOnSave"    – "true"/"false" — half damage on successful save (default true for 5e spells)
///   "healDice"      – healing expression for heal/recovery spells (e.g. "1d8")
///   "healBonus"     – flat healing bonus
///   "healAmount"    – flat HP restored (Fallout stimpak-style items)
///   "spellAttackBonus" – override spell attack roll bonus (optional if caster has systemStats.spellAttackBonus)
///   "spellResolution" – alias for resolution
///   "bonusDice"     – extra d20s in Fallout pools (Luck/AP); useLuck adds +1 (does not spend luckPoints)
///   "rangeModifier" – added to Fallout attack difficulty
///   "cover"         – added to Fallout attack difficulty
/// Multiclass/spellcasting bootstrap lives on systemStats (character_create), NOT here: classLevels, spellcastingAbility, spellSaveDc.
/// </summary>
public class RulesetAction : WorldChange
{
    [Description("ID of the acting character (attacker, caster, skill user, or item user).")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Target character IDs. Required for attack/save/heal spells. List ALL AoE targets in one commit. Omit for non-combat utility/check spells.")]
    [JsonPropertyName("targetIds")]
    public List<string> TargetIds { get; set; } = [];

    [Description("Free-text action label: weapon name, spell name, or skill (e.g. longsword, Fireball, Detect Magic). Attacks: match heldItems for auto weapon merge.")]
    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = default!;

    [Description("Attack, Spell, SkillCheck, SavingThrow (single actor save), ContestedCheck, OpposedCheck (alias of ContestedCheck), UseItem, or Recovery.")]
    [JsonPropertyName("actionType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RulesetActionType ActionType { get; set; }

    [Description("Melee, Ranged, Spell, Maneuver, Social, or Survival. Spell recommended for magic; Social/Survival hints non-combat utility.")]
    [JsonPropertyName("actionCategory")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionCategory ActionCategory { get; set; }

    [Description("5e only: None, Advantage, or Disadvantage for this roll.")]
    [JsonPropertyName("advantageState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdvantageState AdvantageState { get; set; } = AdvantageState.None;

    [Description("Damage type for resistance math (e.g. Fire, Slashing, Radiation).")]
    [JsonPropertyName("damageType")]
    public string? DamageType { get; set; }

    [Description(
        "Resolver hints. Spell: resolution (attack|save|check|utility|heal), dc, save, damageDice, halfOnSave (5e default true), healDice. "
        + "5e/PF2e: bonus, damageBonus, ac, mapPenalty. Fallout: difficulty/dc, attribute, skill, pool, rangeModifier, cover, targetPart, healAmount. "
        + "Engine auto-applies hp deltas from ruleset_action — do not duplicate with separate hp commits.")]
    [JsonPropertyName("parameters")]
    [JsonConverter(typeof(FlexibleStringDictionaryConverter))]
    public Dictionary<string, string> Parameters { get; set; } = [];
}

/// <summary>
/// Create a new location and automatically link it to an existing location.
/// This prevents orphaned locations and counters LLM laziness.
/// </summary>
public class LocationCreate : WorldChange
{
    [Description("The unique ID of the new location (e.g., 'locations/tavern_cellar').")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = default!;

    [Description("The human-readable name of the location (e.g., 'Dank Cellar').")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [Description("A narrative description of the location.")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = default!;

    [Description("The type of location (e.g., 'Room', 'Building', 'Settlement').")]
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LocationType Type { get; set; }

    [Description("Optional. The ID of the parent location that physically contains this one.")]
    [JsonPropertyName("parentLocationId")]
    public string? ParentLocationId { get; set; }

    [Description("Optional but highly recommended. The ID of the location you are coming from. If provided, the engine automatically creates two-way exits linking them.")]
    [JsonPropertyName("connectedFromLocationId")]
    public string? ConnectedFromLocationId { get; set; }

    [Description("Required if connectedFromLocationId is used. Describes the exit from the connected location into this one (e.g., 'A wooden trapdoor leading down').")]
    [JsonPropertyName("connectionDescription")]
    public string? ConnectionDescription { get; set; }

    [Description("Flavor items to return in get_scene (e.g., ['A suspicious crate', 'Rat gnawing on a bone']).")]
    [JsonPropertyName("pointsOfInterest")]
    public List<string> PointsOfInterest { get; set; } = [];

    [Description("Hint for the expected crowd when empty (e.g., '2-3 rats and the occasional drunk').")]
    [JsonPropertyName("ambientCrowd")]
    public string? AmbientCrowd { get; set; }
    
    [Description("Explicit exits. Usually you can leave this empty and rely on connectedFromLocationId instead.")]
    [JsonPropertyName("exits")]
    public List<LocationExit> Exits { get; set; } = [];

    [Description("Set the narrative danger modifier of this location (-50 to +50). Used to seed random encounters.")]
    [JsonPropertyName("dangerModifier")]
    public int DangerModifier { get; set; } = 0;
}

/// <summary>
/// Apply granular updates to an existing location.
/// Useful for opening new paths without full upserts.
/// </summary>
public class LocationUpdate : WorldChange
{
    [Description("The ID of the location to update.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = default!;

    [Description("Append a single exit if the target is not already present.")]
    [JsonPropertyName("addExit")]
    public LocationExit? AddExit { get; set; }

    [Description("Remove an existing exit pointing to this target location ID.")]
    [JsonPropertyName("removeExitTarget")]
    public string? RemoveExitTarget { get; set; }

    [Description("Append a new Point of Interest string.")]
    [JsonPropertyName("addPointOfInterest")]
    public string? AddPointOfInterest { get; set; }

    [Description("Set or clear the ambient crowd. Use empty string to clear.")]
    [JsonPropertyName("ambientCrowd")]
    public string? AmbientCrowd { get; set; }

    [Description("Rename the location.")]
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [Description("Update the description.")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [Description("Change the parent location.")]
    [JsonPropertyName("parentLocationId")]
    public string? ParentLocationId { get; set; }

    [Description("Set the narrative danger modifier (-50 to +50) when the area becomes safer or more dangerous.")]
    [JsonPropertyName("dangerModifier")]
    public int? DangerModifier { get; set; }

    [Description("The new physical state of the location (e.g. 'Roof collapsed, blocking north exit'). Overwrites previous state.")]
    [JsonPropertyName("newState")]
    public string? NewState { get; set; }

    [Description("Temporary visual or atmospheric tags to add (e.g., 'smoky', 'flooded').")]
    [JsonPropertyName("tagsToAdd")]
    public List<string>? TagsToAdd { get; set; }

    [Description("Temporary visual tags to remove.")]
    [JsonPropertyName("tagsToRemove")]
    public List<string>? TagsToRemove { get; set; }

    [Description("Distinctive/permanent features to add (e.g., 'crater in center', 'barricaded windows').")]
    [JsonPropertyName("featuresToAdd")]
    public List<string>? FeaturesToAdd { get; set; }

    [Description("Distinctive features to remove.")]
    [JsonPropertyName("featuresToRemove")]
    public List<string>? FeaturesToRemove { get; set; }
}

/// <summary>
/// Create a new transient (or persistent) character at runtime.
/// </summary>
public class CharacterCreate : WorldChange
{
    [Description("The unique ID of the new character (e.g., 'chars/cloaked_figure_42').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("The name of the character.")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [Description("DM notes about this character.")]
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [Description("The location where the character is right now.")]
    [JsonPropertyName("currentLocationId")]
    public string? CurrentLocationId { get; set; }

    [Description("What they are visibly doing right now.")]
    [JsonPropertyName("currentActivity")]
    public string? CurrentActivity { get; set; }

    [Description("If true, the character will never be auto-deleted even if they have no schedule.")]
    [JsonPropertyName("keepAlive")]
    public bool KeepAlive { get; set; }

    [Description("True for human player characters. Requires campaign context; mutually exclusive with isPartyCompanion.")]
    [JsonPropertyName("isPc")]
    public bool IsPc { get; set; }

    [Description("True for NPC companions on the party roster. Requires campaign context; mutually exclusive with isPc.")]
    [JsonPropertyName("isPartyCompanion")]
    public bool IsPartyCompanion { get; set; }

    [Description("Assigning a schedule makes the character persistent and able to simulate.")]
    [JsonPropertyName("schedule")]
    public Schedule? Schedule { get; set; }

    [Description("Psychological snapshot of the character's desires and fears.")]
    [JsonPropertyName("psychology")]
    public PsychologyProfile? Psychology { get; set; }

    [Description("""
        Maximum hit points. PCs should OMIT this — the bootstrap pipeline derives HP from systemStats + classLevel.
        Creature stat blocks: set maxHp here OR systemStats.statBlockHp (preferred for clarity). Both skip formula derivation.
        If omitted, engine derives when possible; otherwise emits ENGINE WARNING until bootstrapped.
        """)]
    [JsonPropertyName("maxHp")]
    public int? MaxHp { get; set; }

    [Description("Current hit points. If omitted at create, defaults to derived or explicit maxHp. Set alone for wounded state (maxHp still derived).")]
    [JsonPropertyName("currentHp")]
    public int? CurrentHp { get; set; }

    [Description("""
        Ruleset-specific combat and skill stats. REQUIRED for combatants (KeepAlive or maxHp > 0) — engine emits ENGINE WARNING until bootstrapped.
        Include the $system discriminator matching the active campaign ruleset: dnd5e, pf2e, or fallout2d20.
        PC bootstrap (omit maxHp) — engine derives HP, AC, proficiency, etc.:
        - dnd5e: hitDie (e.g. "d12"), level, constitution, optional hpMode (average|rolled)
        - pf2e: classHpPerLevel, ancestryHp, level, constitutionMod
        - fallout2d20: endurance, luck, level, optional hpPerLevel (defaults to endurance)
        Creature stat blocks: statBlockHp on systemStats (or maxHp on this change). Explicit HP skips formula only; AC/proficiency still derive.
        """)]
    [JsonPropertyName("systemStats")]
    public SystemExtension? SystemStats { get; set; }

    [Description("Optional class and level string (e.g. 'Human Fighter 2') to help infer stats when bootstrapping.")]
    [JsonPropertyName("classLevel")]
    public string? ClassLevel { get; set; }
}

/// <summary>
/// Increase a character's level and apply ruleset-specific HP gains.
/// </summary>
public class LevelUpChange : WorldChange
{
    [Description("ID of the character gaining levels (e.g. 'chars/kergil').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("How many levels to gain (default 1).")]
    [JsonPropertyName("levelsGained")]
    public int LevelsGained { get; set; } = 1;

    [Description("Override HP derivation mode for this level gain (5e only: average or rolled).")]
    [JsonPropertyName("hpMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HitPointDerivationMode? HpMode { get; set; }

    [Description("If true, also increase currentHp by the same amount as MaxHp gain (full heal on level). Default false.")]
    [JsonPropertyName("healToMatch")]
    public bool HealToMatch { get; set; }

    [Description("For multiclass PCs: which class gained the level (e.g. 'Wizard', 'Fighter'). Determines hit die for HP gain.")]
    [JsonPropertyName("classGained")]
    public string? ClassGained { get; set; }
}

/// <summary>
/// Change or set a character's schedule, effectively promoting or demoting them.
/// </summary>
public class ScheduleChange : WorldChange
{
    [Description("The ID of the character to update.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("The new schedule. Supplying a schedule promotes a transient NPC to a persistent one. Sending null removes their schedule.")]
    [JsonPropertyName("schedule")]
    public Schedule? Schedule { get; set; }
}

/// <summary>
/// Create a new item (spontaneous loot, generated artifacts) in the world.
/// </summary>
public class ItemCreate : WorldChange
{
    [Description("The unique ID of the new item (e.g., 'items/rusty_locket_19').")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = default!;

    [Description("The name of the item.")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [Description("The description of the item.")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = default!;

    [Description("Where the item currently is (a location ID, character ID, or another item container).")]
    [JsonPropertyName("holderId")]
    public string HolderId { get; set; } = default!;

    [Description("List of structural tags (e.g., ['quest', 'clue']).")]
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [Description("Structural item category (Weapon, Armor, Clothing, etc.). Defaults to Other.")]
    [JsonPropertyName("coreCategory")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemCategory? CoreCategory { get; set; }

    [Description("Key-value attributes for mechanics (e.g., {'value': '5', 'material': 'silver'}).")]
    [JsonPropertyName("properties")]
    [JsonConverter(typeof(FlexibleStringDictionaryConverter))]
    public Dictionary<string, string> Properties { get; set; } = [];
}

/// <summary>
/// Record a party or character travel between two connected locations.
/// </summary>
public class TravelChange : WorldChange
{
    [Description("ID of the character traveling (e.g. 'characters/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;
    
    [Description("ID of the destination location (e.g. 'locations/highpass').")]
    [JsonPropertyName("destinationLocationId")]
    public string DestinationLocationId { get; set; } = default!;
    
    [Description("Narrative summary of the journey.")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }
    
    [Description("Optional travel cost in hours override. If omitted, engine reads from the LocationExit metadata.")]
    [JsonPropertyName("travelCostHoursOverride")]
    public int? TravelCostHoursOverride { get; set; }
    
    [Description("Optional terrain override.")]
    [JsonPropertyName("terrainOverride")]
    public string? TerrainOverride { get; set; }

    [Description("Abstract modifier from -50 to +50 representing the risk of an encounter during this travel. Negative numbers mean safer/stealthy travel (e.g. Pass Without Trace cast, cautious pace). Positive numbers mean reckless/noisy travel (clanking armor, large group).")]
    [JsonPropertyName("encounterRiskModifier")]
    public int? EncounterRiskModifier { get; set; }
}

/// <summary>
/// Adjust a character's reputation with a specific faction.
/// </summary>
public class FactionReputationChange : WorldChange
{
    [Description("The ID of the character.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("The ID of the faction.")]
    [JsonPropertyName("factionId")]
    public string FactionId { get; set; } = default!;

    [Description("Delta to apply to reputation (-100 to +100 range).")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }

    [Description("Reason/narrative for the reputation change.")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Update global stance or influence of a faction.
/// </summary>
public class FactionStateChange : WorldChange
{
    [Description("The ID of the faction.")]
    [JsonPropertyName("factionId")]
    public string FactionId { get; set; } = default!;

    [Description("New stance toward target faction.")]
    [JsonPropertyName("newStance")]
    public FactionStance? NewStance { get; set; }

    [Description("Target faction ID for the stance change.")]
    [JsonPropertyName("targetFactionId")]
    public string? TargetFactionId { get; set; }

    [Description("Delta to apply to faction's influence level (0 to 100).")]
    [JsonPropertyName("influenceDelta")]
    public int? InfluenceDelta { get; set; }

    [Description("Narrative summary of this state change.")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }
}

/// <summary>
/// Create a new structured quest.
/// </summary>
public class QuestCreate : WorldChange
{
    [Description("The unique ID of the quest (e.g. 'quests/rats_01').")]
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = default!;

    [Description("The title of the quest.")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = default!;

    [Description("The ID of the quest giver (character or faction ID).")]
    [JsonPropertyName("giverId")]
    public string? GiverId { get; set; }

    [Description("Initial list of objectives.")]
    [JsonPropertyName("objectives")]
    public List<QuestObjectiveDto> Objectives { get; set; } = [];

    [Description("Optional quest category.")]
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [Description("Quest urgency level (Low, Normal, Urgent, Critical).")]
    [JsonPropertyName("urgency")]
    public QuestUrgency Urgency { get; set; } = QuestUrgency.Normal;

    [Description("IDs of related locations.")]
    [JsonPropertyName("relatedLocationIds")]
    public List<string> RelatedLocationIds { get; set; } = [];

    [Description("IDs of related factions.")]
    [JsonPropertyName("relatedFactionIds")]
    public List<string> RelatedFactionIds { get; set; } = [];

    [Description("DM-only notes for tracking.")]
    [JsonPropertyName("dmNotes")]
    public string? DmNotes { get; set; }

    [Description("Optional ABSOLUTE campaign day (TotalDaysElapsed) by which this quest must be completed to avoid failure consequences.")]
    [JsonPropertyName("deadlineDay")]
    public int? DeadlineDay { get; set; }
}

public class QuestObjectiveDto
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = default!;

    [JsonPropertyName("rewardHint")]
    public string? RewardHint { get; set; }

    [Description("Optional ABSOLUTE campaign day (TotalDaysElapsed) deadline for this objective.")]
    [JsonPropertyName("deadlineDay")]
    public int? DeadlineDay { get; set; }
}

/// <summary>
/// Advance or fail an objective in a quest.
/// </summary>
public class QuestProgress : WorldChange
{
    [Description("The ID of the quest to progress.")]
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = default!;

    [Description("The index of the objective to update (0-based).")]
    [JsonPropertyName("objectiveIndex")]
    public int? ObjectiveIndex { get; set; }

    [Description("The name prefix to match if index is omitted.")]
    [JsonPropertyName("objectiveName")]
    public string? ObjectiveName { get; set; }

    [Description("The new state of the objective.")]
    [JsonPropertyName("newState")]
    public QuestState NewState { get; set; }

    [Description("Narrative summary of progress.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }

    [Description("IDs involved in this progress.")]
    [JsonPropertyName("involvedIds")]
    public List<string>? InvolvedIds { get; set; }
}

/// <summary>
/// Create a new faction.
/// </summary>
public class FactionCreate : WorldChange
{
    [Description("The unique ID of the faction (e.g. 'factions/thieves-guild').")]
    [JsonPropertyName("factionId")]
    public string FactionId { get; set; } = default!;

    [Description("Name of the faction.")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [Description("Description of the faction.")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [Description("Type of the faction.")]
    [JsonPropertyName("factionType")]
    public FactionType FactionType { get; set; } = FactionType.Guild;

    [Description("Optional location ID of the controlling territory.")]
    [JsonPropertyName("controllingTerritory")]
    public string? ControllingTerritory { get; set; }

    [Description("Initial list of territory location IDs controlled.")]
    [JsonPropertyName("territoryLocationIds")]
    public List<string> TerritoryLocationIds { get; set; } = [];

    [Description("Initial list of known leader character IDs.")]
    [JsonPropertyName("knownLeaderIds")]
    public List<string> KnownLeaderIds { get; set; } = [];

    [Description("Optional initial influence level (0-100). Defaults to 50.")]
    [JsonPropertyName("initialInfluenceLevel")]
    public int? InitialInfluenceLevel { get; set; }
}

public class ItemUpdate : WorldChange
{
    [Description("ID of the item being updated.")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = default!;

    [Description("Optional new temporary narrative state of the item (e.g. 'Covered in mud'). Overwrites the previous state.")]
    [JsonPropertyName("newState")]
    public string? NewState { get; set; }

    [Description("Optional new structural category (Weapon, Armor, Clothing, etc.).")]
    [JsonPropertyName("coreCategory")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemCategory? CoreCategory { get; set; }

    [Description("Temporary tags to add to the item (e.g. 'muddy', 'wet').")]
    [JsonPropertyName("tagsToAdd")]
    public List<string>? TagsToAdd { get; set; }

    [Description("Temporary tags to remove from the item.")]
    [JsonPropertyName("tagsToRemove")]
    public List<string>? TagsToRemove { get; set; }

    [Description("Permanent physical features to add (e.g. 'Leather wrapped handle').")]
    [JsonPropertyName("featuresToAdd")]
    public List<string>? FeaturesToAdd { get; set; }

    [Description("Permanent physical features to remove.")]
    [JsonPropertyName("featuresToRemove")]
    public List<string>? FeaturesToRemove { get; set; }

    [Description("Key-value properties to upsert/update.")]
    [JsonPropertyName("propertiesToUpsert")]
    public Dictionary<string, object>? PropertiesToUpsert { get; set; }

    [Description("Keys of properties to delete.")]
    [JsonPropertyName("propertiesToRemove")]
    public List<string>? PropertiesToRemove { get; set; }
}

public class CharacterUpdate : WorldChange
{
    [Description("ID of the character being updated.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Optional new temporary narrative appearance (e.g. 'Looking exhausted and covered in mud'). Overwrites the previous appearance.")]
    [JsonPropertyName("appearanceOverride")]
    public string? AppearanceOverride { get; set; }

    [Description("Temporary visual tags to add. Engine scores these for crowd vulnerability pressure: risky tags include bloody, disheveled, wanted, unarmed, armor_damaged; protective tags include well_armed, escorted, uniform.")]
    [JsonPropertyName("tagsToAdd")]
    public List<string>? TagsToAdd { get; set; }

    [Description("Temporary visual tags to remove.")]
    [JsonPropertyName("tagsToRemove")]
    public List<string>? TagsToRemove { get; set; }

    [Description("Permanent physical features to add (e.g. 'Scar across left eye', 'Dragon tattoo').")]
    [JsonPropertyName("featuresToAdd")]
    public List<string>? FeaturesToAdd { get; set; }

    [Description("Permanent physical features to remove.")]
    [JsonPropertyName("featuresToRemove")]
    public List<string>? FeaturesToRemove { get; set; }

    [Description("Set to true to protect this character from transient eviction (use on quest givers and important NPCs).")]
    [JsonPropertyName("keepAlive")]
    public bool? KeepAlive { get; set; }

    [Description("Set true to mark as a human PC, or false to clear. Requires campaign-tagged character.")]
    [JsonPropertyName("isPc")]
    public bool? IsPc { get; set; }

    [Description("Set true to mark as an NPC party companion, or false to clear. Requires campaign-tagged character.")]
    [JsonPropertyName("isPartyCompanion")]
    public bool? IsPartyCompanion { get; set; }

    [Description("Partial ruleset stats merge. Same shape as character_create.systemStats.")]
    [JsonPropertyName("systemStats")]
    public SystemExtension? SystemStats { get; set; }
}

/// <summary>
/// Patch or bootstrap a character's ruleset-specific systemStats (partial merge).
/// </summary>
public class SystemStatsChange : WorldChange
{
    [Description("ID of the character to update.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Ruleset stats to merge. Include $system discriminator (dnd5e, pf2e, fallout2d20).")]
    [JsonPropertyName("systemStats")]
    public SystemExtension SystemStats { get; set; } = default!;
}

public class KnowledgeUpdate : WorldChange
{
    [Description("ID of the character whose memory is updating.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("The topic/entity the memory is about (e.g. 'The Rusty Tavern', 'Mayor Bob').")]
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = default!;

    [Description("The details of the memory. Write how the character understands it now.")]
    [JsonPropertyName("details")]
    public string Details { get; set; } = default!;

    [Description("Optional. If provided, overrides the memory's importance level (Trivial, Important, Core).")]
    [JsonPropertyName("importance")]
    public MemoryImportance? Importance { get; set; }

    [Description("Default true. Set false to skip creating or updating the memory graph for this topic.")]
    [JsonPropertyName("createMemory")]
    public bool CreateMemory { get; set; } = true;

    [Description("Optional structured enrichment. Overrides inference when provided.")]
    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MemorySource? Source { get; set; }

    [Description("Optional structured enrichment. Overrides inference when provided.")]
    [JsonPropertyName("valence")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmotionalValence? Valence { get; set; }

    [Description("Optional structured enrichment (0.0–1.0). Overrides default salience when provided.")]
    [JsonPropertyName("salience")]
    public double? Salience { get; set; }

    [Description("Optional structured enrichment. Overrides default urgency when provided.")]
    [JsonPropertyName("urgency")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MemoryUrgency? Urgency { get; set; }

    [Description("Optional entity IDs this memory relates to (characters, items, locations).")]
    [JsonPropertyName("relatedEntityIds")]
    public List<string>? RelatedEntityIds { get; set; }
}
