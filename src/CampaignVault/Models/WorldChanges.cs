using System.ComponentModel;
using System.Text.Json.Serialization;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Models;

/// <summary>
/// Base for all atomic, composable world mutations that can be sent to <c>commit</c>.
/// The LLM must include the exact <c>$type</c> discriminator so the server knows which concrete change to apply.
/// Mix as many different change kinds as needed in a single call for atomicity.
/// </summary>
[Description("REQUIRED: Every WorldChange object MUST include the exact '$type' discriminator field. Valid values: hp, activity, relationship, need, event, status, resource, rumor, quest_progress, plot_thread_progress, plot_thread_clue, travel, rest, location_update, character_update, system_stats, and 30+ others. Omitting '$type' will cause deserialization to fail. Use 'system_stats' (or character_update's systemStats field) to bootstrap/patch a character's ruleset combat stats — see IncompleteSystemStats ENGINE WARNING. Mix freely (hp + activity + relationship + need + event, etc.) for atomicity.")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HpChange), "hp")]
[JsonDerivedType(typeof(ItemTransfer), "item")]
[JsonDerivedType(typeof(StatusChange), "status")]
[JsonDerivedType(typeof(StatusRemove), "statusremove")]
[JsonDerivedType(typeof(EventOccurred), "event")]
[JsonDerivedType(typeof(RumorEvolves), "rumor")]
[JsonDerivedType(typeof(RelationshipChange), "relationship")]
[JsonDerivedType(typeof(NeedChange), "need")]
[JsonDerivedType(typeof(AttributeChange), "attribute")]
[JsonDerivedType(typeof(MoodChange), "mood")]
[JsonDerivedType(typeof(ActivityChange), "activity")]
[JsonDerivedType(typeof(RulesetAction), "ruleset_action")]
[JsonDerivedType(typeof(LocationUpdate), "location_update")]
[JsonDerivedType(typeof(LevelUpChange), "level_up")]
[JsonDerivedType(typeof(ScheduleChange), "schedule_change")]
[JsonDerivedType(typeof(TravelChange), "travel")]
[JsonDerivedType(typeof(FactionReputationChange), "faction_reputation")]
[JsonDerivedType(typeof(FactionStateChange), "faction_state")]
[JsonDerivedType(typeof(QuestProgress), "quest_progress")]
[JsonDerivedType(typeof(RestChange), "rest")]
[JsonDerivedType(typeof(ItemUpdate), "item_update")]
[JsonDerivedType(typeof(CharacterUpdate), "character_update")]
[JsonDerivedType(typeof(SystemStatsChange), "system_stats")]
[JsonDerivedType(typeof(KnowledgeUpdate), "knowledge_update")]
[JsonDerivedType(typeof(EngagementRelationChange), "engagement_relation")]
[JsonDerivedType(typeof(SpatialPositionChange), "spatial_position")]
[JsonDerivedType(typeof(SceneSetupChange), "scene_setup")]
[JsonDerivedType(typeof(SceneInterruptCheck), "scene_interrupt_check")]
[JsonDerivedType(typeof(PlotThreadProgress), "plot_thread_progress")]
[JsonDerivedType(typeof(PlotThreadClueDiscovered), "plot_thread_clue")]
[JsonDerivedType(typeof(ResourceChange), "resource")]
[JsonDerivedType(typeof(RestRecoveryAck), "rest_recovery_ack")]
[JsonDerivedType(typeof(ArchiveEntityChange), "archive_entity")]
[JsonDerivedType(typeof(ItemEquip), "item_equip")]
[JsonDerivedType(typeof(ItemUnequip), "item_unequip")]
[JsonDerivedType(typeof(ItemUse), "item_use")]
[JsonDerivedType(typeof(ItemPersistenceSurfaced), "item_persistence_surfaced")]
[JsonDerivedType(typeof(MemoryDecay), "memory_decay")]
[JsonDerivedType(typeof(CampaignUpdateChange), "campaign_update")]
[JsonDerivedType(typeof(WorldEventStatusChange), "world_event_status")]
[JsonDerivedType(typeof(XpGrantChange), "xp_grant")]
public abstract class WorldChange
{
    /// <summary>
    /// Internal flag: true if this change was authored by the simulation engine (not by LLM commit or manual authoring).
    /// Not exposed in JSON. Used to skip certain side-effects (e.g., pressure cooldown resets, staleness updates).
    /// </summary>
    [JsonIgnore]
    public bool IsEngineAuthored { get; set; }

    /// <summary>
    /// Optional: roughly how many minutes of in-game time this specific change's narrative beat took
    /// (a few lines of dialogue, a lockpicking attempt, a combat round, sharing a meal, ...). Summed
    /// across every change in the commit batch and applied as a small needs nudge (hunger/thirst/
    /// tiredness) to the characters involved in the batch — no need to wait for rest or advance_world
    /// for needs to move during an ordinary scene. Ignored on RestChange/TravelChange — their own
    /// hour-based duration fields already advance time; use those instead for rest/travel.
    /// </summary>
    [Description("Roughly how many minutes this beat took (dialogue, lockpicking, a combat round, a meal...). Summed across the batch to nudge hunger/thirst/tiredness for characters involved. Omit for instantaneous changes. Ignored on rest/travel changes — use their own hour fields instead.")]
    [JsonPropertyName("minutesElapsed")]
    public int? MinutesElapsed { get; set; }
}

/// <summary>
/// Attempt to pass time resting. The engine calculates the danger of the location and the LLM's security modifier
/// to determine if the rest is interrupted by an encounter.
/// </summary>
public class RestChange : WorldChange
{
    [Description("ID of the character attempting to rest.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the location where they are resting.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = null!;

    [Description("How many hours the character intends to rest. (e.g., 1 for short, 8 for long). Eligible resource pools (spell slots, etc.) recover immediately when the rest completes — no separate advance_world call needed.")]
    [JsonPropertyName("intendedHours")]
    public int IntendedHours { get; set; }

    [Description("Modifier representing the safety of the setup (-50 to +50). E.g., +20 for stealthy hidden camp, +100 for Tiny Hut, -20 for drunk in an alley.")]
    [JsonPropertyName("securityModifier")]
    public int SecurityModifier { get; set; }

    [Description("Optional: Explicit rest type (LongRest, ShortRest, PerTurn). If omitted, engine infers from intendedHours: 8+ = LongRest, 1-7 = ShortRest.")]
    [JsonPropertyName("restType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RestType? RestType { get; set; }

    [Description("Narrative description of how the character rests.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }
}

/// <summary>
/// Grant or deduct experience points to a character.
/// </summary>
public class XpGrantChange : WorldChange
{
    [Description("ID of the character receiving XP (e.g., 'chars/hero').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Amount of XP to grant (positive) or deduct (negative).")]
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [Description("Source of the XP grant.")]
    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public XpSource Source { get; set; } = XpSource.Combat;

    [Description("Narrative reason for the XP grant (e.g., 'defeated goblin chief', 'completed quest').")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Source category for XP grants.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum XpSource
{
    Combat,
    Exploration,
    Social,
    Milestone,
    DMFiat
}

/// <summary>
/// Optional single-roll check for whether someone from the ambient crowd interrupts the scene.
/// LLM calls this after tense beats in crowded locations — not on every line of dialog.
/// ENGINE MACRO: Rolls crowd reaction internally and may emit a derived ActivityChange promoting
/// a transient from the crowd. Cooldown: one interrupt per location per day.
/// </summary>
public class SceneInterruptCheck : WorldChange
{
    [Description("ID of the location where the scene beat occurred.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = null!;

    [Description("ID of the character whose vulnerability/flavor drives the check (usually the PC).")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Abstract modifier from -50 to +50 representing crowd reaction risk, like encounterRiskModifier on travel. Positive = PC looks vulnerable/provocative (bloodied, wanted, insulted a guard). Negative = PC looks safe (well_armed, escorted). If omitted, engine auto-derives from visualTags/appearance/equipment.")]
    [JsonPropertyName("riskModifier")]
    public int? RiskModifier { get; set; }

    [Description("Optional free-text flavor for the engine directive (e.g. 'Bloodied wanted face, Schlag drawn, crowd already hostile') - include when narratively makes sense.")]
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>Adjust a character's current HP by a delta. Positive heals, negative damages.</summary>
public class HpChange : WorldChange
{
    [Description("ID of the character whose HP to modify (e.g. 'chars/grog' or 'chars/elara-voss'). Must exist.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Delta to apply to CurrentHp. Positive = heal/gain, negative = damage/loss. Use small values for normal hits, larger for big effects.")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }
}

/// <summary>Move/transfer an existing item to a new holder (character, location, or another container item).</summary>
public class ItemTransfer : WorldChange
{
    [Description("ID of the item being moved (e.g. 'items/iron-key-17').")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;

    [Description("New holder ID. Can be a character ('chars/xxx'), a location ('locations/xxx'), or another item acting as container.")]
    [JsonPropertyName("toHolderId")]
    public string ToHolderId { get; set; } = null!;
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
    [Description("ID of the character receiving the status (e.g. 'chars/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("[Preferred] Structured StatusEffect: name, category, optional conditionName/affectedPart/statModifiers, and expiresAtDay/expiresAtRound (omit both for permanent). See get_help topic=combat for the full field reference.")]
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
    [Description("ID of the character whose status to remove (e.g. 'chars/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Name of the status condition to remove. Matching is case-insensitive; all matching entries are removed.")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = null!;
}

/// <summary>
/// Record a noteworthy occurrence in the world. Use Category='Unresolved' for open plot threads the party should care about.
/// These appear in get_scene, recall_history, and get_world_state.
/// </summary>
public class EventOccurred : WorldChange
{
    [Description("Short human-readable summary of what happened. This becomes the main text of the event log entry.")]
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = null!;

    [Description("Classification of the event. Use 'Unresolved' for dangling plot hooks the party should follow up on. Other good values: 'Combat', 'Conversation', 'Discovery', 'Arrival', 'Betrayal', 'SceneInterrupt' (engine-emitted crowd interrupts).")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventCategory Category { get; set; } = EventCategory.Unresolved;

    [Description("Character IDs of everyone who participated. REQUIRED when category is 'Conversation'. Field name is 'involved', not 'participants'. See get_help topic=patterns.")]
    [JsonPropertyName("involved")]
    public List<string>? Involved { get; set; }

    [Description("Optional emotional beat for relational initiative, e.g. 'gratitude', 'affection', 'betrayal', 'gift_received'.")]
    [JsonPropertyName("emotionalBeat")]
    public string? EmotionalBeat { get; set; }

    [Description("Optional. Item, character, or location ID this beat relates to.")]
    [JsonPropertyName("relatedEntityId")]
    public string? RelatedEntityId { get; set; }

    [Description("Optional. Primary location ID where the event occurred. Never put a location ID inside 'involved' — use this field.")]
    [JsonPropertyName("locationId")]
    public string? LocationId { get; set; }

    [Description("Optional. Additional location IDs touched by a spillover beat, e.g. a bar fight that spills from the tavern into the alley outside.")]
    [JsonPropertyName("relatedLocationIds")]
    public List<string>? RelatedLocationIds { get; set; }

    [Description("Optional client-chosen ID for this event (e.g. 'events/tavern-bar-fight-001'), so other changes in the SAME commit batch (e.g. knowledge_update.sourceEventIds) can reference it. Omit to let the engine generate one automatically — most events should omit this.")]
    [JsonPropertyName("eventId")]
    public string? EventId { get; set; }

    [Description("Optional importance (Trivial/Important/Core; Core always survives retrieval budgets). If omitted, inferred from category.")]
    [JsonPropertyName("importance")]
    public MemoryImportance? Importance { get; set; }

    [Description("Deliberate (explicit player act, locks in importance) or Passive (ambient, decays naturally). Omit for Passive. See get_help topic=patterns.")]
    [JsonPropertyName("recordingMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordingMode? RecordingMode { get; set; }

    [Description("Optional structured data about the event (e.g. { \"hoursTraveled\": 2.5 } for travel). Stored as-is on the event's Details field.")]
    [JsonPropertyName("details")]
    public Dictionary<string, object>? Details { get; set; }
}

/// <summary>Change the state of an existing rumor and optionally rewrite its current text.</summary>
public class RumorEvolves : WorldChange
{
    [Description("ID of the rumor to evolve (e.g. 'rumors/bandit-activity-in-woods'). Must already exist.")]
    [JsonPropertyName("rumorId")]
    public string RumorId { get; set; } = null!;

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
    public string RumorId { get; set; } = null!;

    [Description("Short topic label (e.g. 'Nightshade Gang').")]
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = null!;

    [Description("Initial rumor text heard by the party or world.")]
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [Description("Optional location IDs this rumor is tied to. First ID becomes RegionLocationId; omit for global.")]
    [JsonPropertyName("relatedLocationIds")]
    public List<string>? RelatedLocationIds { get; set; }
}

/// <summary>Apply a numeric delta to the relationship score between two characters. Range is typically -100 to +100.</summary>
public class RelationshipChange : WorldChange
{
    [Description("ID of the character whose opinion of the target is changing (e.g. 'chars/elara-voss').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the target character being evaluated (e.g. 'chars/bram-ironarm').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = null!;

    [Description("Numeric delta to apply to the relationship score. Positive = better opinion/trust, negative = worse. Typical range -20 to +20 per significant event.")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }

    [Description("Narrative reason for the shift. This is stored with the relationship and helps the behavioral synthesizer explain why the NPC feels this way.")]
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// Establish, update, or remove a pairwise engagement state between two entities (grapple, embrace, watch, etc.).
/// For zone/distance positioning, use a future spatial-position change instead.
/// </summary>
public class EngagementRelationChange : WorldChange
{
    [Description("ID of the character initiating or anchoring the relation (e.g. 'chars/bard').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the target character or object (e.g. 'chars/archivist').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = null!;

    [Description("Engagement category: Physical, Social, Medical, Attention, or Proximity.")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementCategory? Category { get; set; }

    [Description("Freeform verb (e.g. 'grappling', 'ranting at', 'stitching'). Omit or set to null/empty to remove/clear the relation.")]
    [JsonPropertyName("verb")]
    public string? Verb { get; set; }

    [Description("Optional restriction override: None, Soft, or Hard.")]
    [JsonPropertyName("restrictionLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementRestrictionLevel? RestrictionLevel { get; set; }

    [Description("Whether to automatically establish the inverse relationship on the target (e.g., if the character grapples the target, the target becomes grappled-by the character).")]
    [JsonPropertyName("bidirectional")]
    public bool Bidirectional { get; set; } = true;
}

/// <summary>
/// Establish, update, or remove relative zone/distance positioning for a character.
/// For pairwise grapple/embrace anchors, use <see cref="EngagementRelationChange"/> instead.
/// </summary>
public class SpatialPositionChange : WorldChange
{
    [Description("ID of the character whose position is being set (e.g. 'chars/drunk').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the reference entity (e.g. 'chars/pc', 'locations/tavern_bar').")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = null!;

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

/// <summary>
/// Composite convenience for setting engagement and/or spatial position against the same target
/// in a single commit item — e.g. "AI DM sets up a scene" in one call instead of two. Internally
/// dispatches to the same EngagementRelationChange/SpatialPositionChange handlers, so behavior
/// (bidirectional mirroring, no-op detection, history logging) is identical either way. Prefer the
/// bare engagement_relation/spatial_position types for single-purpose updates.
/// </summary>
public class SceneSetupChange : WorldChange
{
    [Description("ID of the character whose engagement/position is being set (e.g. 'chars/bard').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the target character or object this scene setup is relative to (e.g. 'chars/archivist'). Shared by both Engagement and Spatial below.")]
    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = null!;

    [Description("Optional pairwise engagement to establish/update/clear against TargetId. Omit to leave engagement untouched.")]
    [JsonPropertyName("engagement")]
    public SceneSetupEngagement? Engagement { get; set; }

    [Description("Optional relative spatial placement against TargetId. Omit to leave spatial position untouched.")]
    [JsonPropertyName("spatial")]
    public SceneSetupSpatial? Spatial { get; set; }
}

public class SceneSetupEngagement
{
    [Description("Engagement category: Physical, Social, Medical, Attention, or Proximity.")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementCategory? Category { get; set; }

    [Description("Freeform verb (e.g. 'grappling', 'ranting at', 'stitching'). Omit or set to null/empty to remove/clear the relation.")]
    [JsonPropertyName("verb")]
    public string? Verb { get; set; }

    [Description("Optional restriction override: None, Soft, or Hard.")]
    [JsonPropertyName("restrictionLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EngagementRestrictionLevel? RestrictionLevel { get; set; }

    [Description("Whether to automatically establish the inverse relationship on the target. Default true.")]
    [JsonPropertyName("bidirectional")]
    public bool Bidirectional { get; set; } = true;
}

public class SceneSetupSpatial
{
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

/// <summary>Adjust one of a character's open-ended psychological or physical needs (hunger, thirst, tiredness; paranoia, obsession, wanderlust, bloodlust, guilt, despair, or other custom needs).</summary>
public class NeedChange : WorldChange
{
    [Description("ID of the character whose need is changing (e.g. 'chars/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Name of the need (unrestricted — invent any narrative-appropriate need, e.g. 'paranoia', 'bloodlust'; core needs are 'hunger'/'thirst'/'tiredness').")]
    [JsonPropertyName("need")]
    public string Need { get; set; } = null!;

    [Description("Delta to apply. Negative values satisfy/reduce the need (e.g. feeding someone). Positive values increase the drive (e.g. marching all day raises tiredness).")]
    [JsonPropertyName("delta")]
    public float Delta { get; set; }
}

/// <summary>Set or delta an arbitrary narrative attribute on a character (willpower, temperature, morale, corruption, reputation, etc.).</summary>
public class AttributeChange : WorldChange
{
    [Description("ID of the character receiving the attribute change.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Name of the attribute. Common examples: 'willpower', 'temperature', 'morale', 'corruption', 'reputation', 'fear', 'exhaustion_level' (D&D 5e mechanical exhaustion, 1-6 scale — distinct from narrative tiredness set via 'need' commits). Invent others that fit the story.")]
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = null!;

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
    public string CharacterId { get; set; } = null!;

    [Description("New short mood string (e.g. 'grimly determined', 'euphoric', 'terrified', 'playfully drunk', 'brooding'). Keep it evocative but concise.")]
    [JsonPropertyName("newMood")]
    public string NewMood { get; set; } = null!;
}

/// <summary>
/// Force-update what an NPC is currently doing and/or where they are physically located.
/// This immediately affects what get_scene returns for that NPC's CurrentActivity and CurrentLocationId.
/// Use liberally at the end of roleplay or combat so the world model stays in sync with the story.
/// </summary>
public class ActivityChange : WorldChange
{
    [Description("ID of the character whose activity/location is changing (e.g. 'chars/bram-ironarm').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

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

    [Description("Optional: name of a Point of Interest to materialize on newLocationId in this same step (same effect as location_update's materializePointOfInterest). Pair with poiDetails. Requires newLocationId/updateLocation to also be set.")]
    [JsonPropertyName("poiName")]
    public string? PoiName { get; set; }

    [Description("The persistent detail/state for poiName — sensory and tactical specifics that matter later (cover, water, tracks, hazards, what's hidden there). Only applied if poiName is also set.")]
    [JsonPropertyName("poiDetails")]
    public string? PoiDetails { get; set; }
}

/// <summary>
/// Trigger a ruleset-specific action (attack, skill check, contested roll, recovery, etc.).
/// The active IRulesetResolver (selected from CampaignConfig.ActiveSystem) handles the math
/// and returns primitive WorldChange mutations (HpChange, StatusChange, NeedChange, etc.)
/// back into the StageChangesAsync pipeline.
///
/// ENGINE SIDE EFFECTS: This action auto-applies HpChange, StatusChange, and EngagementRelationChange
/// as derived mutations. Do NOT add separate hp/status commits for the same action — that causes double-application.
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
///   "difficulty"    – alias for "dc" on Spell actions
///   "advantage"     – "true"/"false" for D&amp;D 5e
///   "item"          – item document ID for UseItem actions (alias: weaponItemId, weapon)
///   "weaponItemId"  – held weapon item ID; properties merge into attack if parameters omitted
///   "attackCount"   – max separate attack rolls (multi-target / repeating weapons); defaults to targetIds.Count when multiple targets listed
///   "initiativeSkill" – skill name overriding default initiative (PF2e)
///   "targetSkill"   – skill name for the target side of a ContestedCheck
///   "resolution"    – spell routing: attack, save, check, utility, heal (alias: spellResolution)
///   "save"          – saving throw ability for save-based spells (5e/PF2e)
///   "halfOnSave"    – "true"/"false" — half damage on successful save (default true for 5e spells)
///   "healDice"      – healing expression for heal/recovery spells (e.g. "1d8")
///   "healBonus"     – flat healing bonus
///   "healAmount"    – flat HP restored (no dice roll)
///   "spellAttackBonus" – override spell attack roll bonus (optional if caster has systemStats.spellAttackBonus)
///   "spellResolution" – alias for resolution
/// Multiclass/spellcasting bootstrap lives on systemStats (character_create), NOT here: classLevels, spellcastingAbility, spellSaveDc.
/// </summary>
public class RulesetAction : WorldChange
{
    [Description("ID of the acting character (attacker, caster, skill user, or item user).")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Target character IDs. Required for attack/save spells. Omit for a self-targeted heal (heals characterId) or for non-combat utility/check spells. List ALL AoE targets in one commit.")]
    [JsonPropertyName("targetIds")]
    public List<string> TargetIds { get; set; } = [];

    [Description("Free-text action label: weapon name, spell name, or skill (e.g. longsword, Fireball, Detect Magic). Attacks: match heldItems for auto weapon merge.")]
    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = null!;

    [Description("REQUIRED (no default; omitting fails the commit). Attack, Spell, SkillCheck, SavingThrow, ContestedCheck (alias OpposedCheck), UseItem, or Recovery.")]
    [JsonPropertyName("actionType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required RulesetActionType ActionType { get; set; }

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

    [Description("True if this action is being taken as a reaction (e.g. opportunity attack, counterspell) rather than consuming the actor's normal turn action. Consumes the reaction slot/flag instead of the action slot.")]
    [JsonPropertyName("isReaction")]
    public bool IsReaction { get; set; }

    [Description("Optional: why a reaction fired, e.g. 'opportunity_attack', 'readied_action'. Narrative/audit only, not required for resolution.")]
    [JsonPropertyName("reactionTrigger")]
    public string? ReactionTrigger { get; set; }

    [Description("Resolver-specific parameters (dc, bonus, damageDice, save, resolution, etc.) — see get_help topic=combat for the full key reference per ruleset. Engine auto-applies hp deltas; don't duplicate with a separate hp commit.")]
    [JsonPropertyName("parameters")]
    [JsonConverter(typeof(FlexibleStringDictionaryConverter))]
    public Dictionary<string, string> Parameters { get; set; } = [];
}

/// <summary>
/// Create a new location and automatically link it to an existing location.
/// This prevents orphaned locations and counters LLM laziness.
/// </summary>
/// <summary>
/// Apply granular updates to an existing location.
/// Useful for opening new paths without full upserts.
/// </summary>
public class LocationUpdate : WorldChange
{
    [Description("The ID of the location to update.")]
    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = null!;

    [Description("Append a single exit if the target is not already present.")]
    [JsonPropertyName("addExit")]
    public LocationExit? AddExit { get; set; }

    [Description("Remove an existing exit pointing to this target location ID.")]
    [JsonPropertyName("removeExitTarget")]
    public string? RemoveExitTarget { get; set; }

    [Description("Append a new Point of Interest string (light flavor). Can be paired with pointOfInterestDetails map to add with initial details.")]
    [JsonPropertyName("addPointOfInterest")]
    public string? AddPointOfInterest { get; set; }

    [Description("Remove a Point of Interest entirely (e.g. the board was burned down, poster ripped and taken). Also removes any associated details.")]
    [JsonPropertyName("removePointOfInterest")]
    public string? RemovePointOfInterest { get; set; }

    [Description("Name of a Point of Interest (existing or new) to give/update persistent details. Use with poiDetails. Re-applying to an existing PoI updates its state (e.g. after ripping a poster or setting it on fire).")]
    [JsonPropertyName("materializePointOfInterest")]
    public string? MaterializePointOfInterest { get; set; }

    [Description("The persistent details, description, or current state for the PoI. When used with materializePointOfInterest or in the map, this records what the PoI is like now.")]
    [JsonPropertyName("poiDetails")]
    public string? PoiDetails { get; set; }

    [Description("Map of PoI name → current details. Can add, update, or replace details for multiple PoIs. Keys not already in PointsOfInterest will be added.")]
    [JsonPropertyName("pointOfInterestDetails")]
    public Dictionary<string, string>? PointOfInterestDetails { get; set; }

    [Description("Set or clear the ambient crowd. Use empty string to clear. Always set when narratively justified. Taverns, for example, should always have some sort of a crowd - if active, of course")]
    [JsonPropertyName("ambientCrowd")]
    public string? AmbientCrowd { get; set; }

    [Description("Rename the location.")]
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [Description("Update the description. Required field - this would serve as hints for DM to describe the location")]
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

    [Description("Record a transient NPC departure on this location (engine-internal; caps list at 10, most recent first).")]
    [JsonPropertyName("recordDeparture")]
    public DepartedNpcRecord? RecordDeparture { get; set; }
}

/// <summary>
/// Create a new transient (or persistent) character at runtime.
/// </summary>
public class CharacterCreate : WorldChange
{
    [Description("The unique ID of the new character (e.g., 'chars/cloaked_figure_42').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("The name of the character.")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

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

    [Description("PCs should OMIT — derived from systemStats + classLevel. Creatures: set here or systemStats.statBlockHp to skip formula derivation.")]
    [JsonPropertyName("maxHp")]
    public int? MaxHp { get; set; }

    [Description("Current hit points. If omitted at create, defaults to derived or explicit maxHp. Set alone for wounded state (maxHp still derived).")]
    [JsonPropertyName("currentHp")]
    public int? CurrentHp { get; set; }

    [Description("Ruleset-specific stats with $system discriminator (dnd5e/pf2e). REQUIRED for combatants — engine warns until bootstrapped. See get_help topic=world-building for the per-ruleset bootstrap field list.")]
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
    public string CharacterId { get; set; } = null!;

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

    [Description("Optional narrative reason logged in the commit summary (e.g. 'defeated the goblin chief', 'milestone after clearing the mine').")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Change or set a character's schedule, effectively promoting or demoting them.
/// </summary>
public class ScheduleChange : WorldChange
{
    [Description("The ID of the character to update.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("The new schedule. Supplying a schedule promotes a transient NPC to a persistent one. Sending null removes their schedule.")]
    [JsonPropertyName("schedule")]
    public Schedule? Schedule { get; set; }
}

/// <summary>
/// Create a new item (spontaneous loot, generated artifacts) in the world.
/// </summary>
/// <summary>
/// Record a party or character travel between two connected locations.
/// </summary>
public class TravelChange : WorldChange
{
    [Description("ID of the character traveling (e.g. 'chars/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;
    
    [Description("ID of the destination location (e.g. 'locations/highpass').")]
    [JsonPropertyName("destinationLocationId")]
    public string DestinationLocationId { get; set; } = null!;
    
    [Description("Narrative summary of the journey.")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }
    
    [Description("Optional travel cost in hours override. Supports decimal values (e.g., 0.5 for 30 minutes). If omitted, engine reads from the LocationExit metadata.")]
    [JsonPropertyName("travelCostHoursOverride")]
    public double? TravelCostHoursOverride { get; set; }
    
    [Description("Optional terrain override. Use this to designate 'special' terrain that may or may not cause damage or side effects")]
    [JsonPropertyName("terrainOverride")]
    public string? TerrainOverride { get; set; }

    [Description("Abstract modifier from -50 to +50 representing the risk of an encounter during this travel. Should always be set if narratively justified (either by plot, situation or player actions - such as declaring caution or not declaring caution - never assume player intent). Negative numbers mean safer/stealthy travel (e.g. Pass Without Trace cast, cautious pace). Positive numbers mean reckless/noisy travel (clanking armor, large group).")]
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
    public string CharacterId { get; set; } = null!;

    [Description("The ID of the faction.")]
    [JsonPropertyName("factionId")]
    public string FactionId { get; set; } = null!;

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
    public string FactionId { get; set; } = null!;

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
/// <summary>
/// Advance or fail an objective in a quest.
/// </summary>
public class QuestProgress : WorldChange
{
    [Description("The ID of the quest to progress.")]
    [JsonPropertyName("questId")]
    public string QuestId { get; set; } = null!;

    [Description("The index of the objective to update (0-based).")]
    [JsonPropertyName("objectiveIndex")]
    public int? ObjectiveIndex { get; set; }

    [Description("The name prefix to match if index is omitted.")]
    [JsonPropertyName("objectiveName")]
    public string? ObjectiveName { get; set; }

    [Description("REQUIRED. The new state of the objective. Must be sent explicitly — omitting this key fails the commit rather than silently resetting the objective to Open.")]
    [JsonPropertyName("newState")]
    public required QuestState NewState { get; set; }

    [Description("Narrative summary of progress.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }

    [Description("Optional. Character/faction/location IDs tied to this progress step. When the quest reaches Complete/Failed, these become the 'involved' list on the auto-generated completion event — omit and that event will have no 'involved' entries.")]
    [JsonPropertyName("involvedIds")]
    public List<string>? InvolvedIds { get; set; }
}

/// <summary>
/// Create a new faction.
/// </summary>
public class ItemUpdate : WorldChange
{
    [Description("ID of the item being updated.")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;

    [Description("Optional new temporary narrative state of the item (e.g. 'Covered in mud'). Overwrites the previous state.")]
    [JsonPropertyName("newState")]
    public string? NewState { get; set; }

    [Description("Optional new structural category (Weapon, Armor, Clothing, etc.).")]
    [JsonPropertyName("coreCategory")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemCategory? CoreCategory { get; set; }

    [Description("Temporary tags to add (e.g. 'muddy', 'wet'). Convention for open-carry/concealed display: tag the container, not the contents. See get_help topic=visual-sandbox.")]
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

    [Description("Narrative note about why this item was left behind. Pair with ambientExpiresAtDay so the engine can nag about stale debris (never auto-moves/deletes).")]
    [JsonPropertyName("ambientPersistenceNote")]
    public string? AmbientPersistenceNote { get; set; }

    [Description("Campaign day after which the engine nags about this item's ambient fate. Setting this clears any previously-surfaced nag.")]
    [JsonPropertyName("ambientExpiresAtDay")]
    public float? AmbientExpiresAtDay { get; set; }

    [Description("Create or update a durable, examine-able detail on this item (scratches, stains, secret compartments) — not temporary tags or narrative flavor. See get_help topic=visual-sandbox for the full field reference.")]
    [JsonPropertyName("upsertItemDetail")]
    public ItemDetailUpsertRequest? UpsertItemDetail { get; set; }

    [Description("Marks an existing ItemDetail (by its id) as retired — sets isRetired=true and a terminal status. Does NOT delete the record, so any memory referencing it stays resolvable. Use when a detail is no longer true (compartment discovered and emptied, stain cleaned) rather than pretending it never existed.")]
    [JsonPropertyName("retireItemDetailId")]
    public string? RetireItemDetailId { get; set; }
}

/// <summary>
/// Request to create or update a single <see cref="ItemDetail"/> on an item. Used both via
/// <see cref="ItemUpdate.UpsertItemDetail"/> (commit path, existing items) and via
/// <c>ItemUpsertRequest.ItemDetails</c> (world_build/upsert_item, creation only — id and
/// participants are ignored there since a new item has no existing details to match and no
/// in-fiction moment for a memory push).
/// </summary>
public class ItemDetailUpsertRequest
{
    [Description("Optional. Pass if known (from a prior take_turn response or get_entity) for a cheap authoritative match. Omit to resolve by semantic similarity or create new.")]
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [Description("Short label for the detail (e.g. 'Hidden compartment', 'Scorch mark').")]
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [Description("Full narrative description of the detail's current state.")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = null!;

    [Description("Optional short current-status label, distinct from the full description (e.g. 'Concealed', 'Discovered').")]
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [Description("DM-only guidance for narrating/adjudicating this detail (suggested DC, discovery conditions, ongoing effects). Never shown to players. See get_help topic=visual-sandbox for examples.")]
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    [Description("Optional origin of this detail — what caused/created it.")]
    [JsonPropertyName("origin")]
    public ItemDetailOrigin? Origin { get; set; }

    [Description("Optional id of whatever this detail is currently physically anchored to (location/item/character) — purely descriptive, not engine-enforced. Pass \"\" to clear once freed. See get_help topic=visual-sandbox.")]
    [JsonPropertyName("tetheredToId")]
    public string? TetheredToId { get; set; }

    [Description("Optional characters who caused/witnessed this detail — pushes a memory to each. Ignored inside world_build (no in-fiction moment yet).")]
    [JsonPropertyName("participants")]
    public List<ItemDetailParticipant>? Participants { get; set; }

    [Description("Optional days of no updates before the engine nudges a review (default 60). Set shorter for fast-changing details, longer for near-permanent ones.")]
    [JsonPropertyName("reviewIntervalDays")]
    public int? ReviewIntervalDays { get; set; }
}

public class CharacterUpdate : WorldChange
{
    [Description("ID of the character being updated.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

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

    [Description("Set when a transient NPC departs (engine eviction). Cleared automatically when the character is re-anchored to a location.")]
    [JsonPropertyName("departedAtDay")]
    public int? DepartedAtDay { get; set; }

    [Description("Location the character departed from. Pair with departedAtDay.")]
    [JsonPropertyName("departedFromLocationId")]
    public string? DepartedFromLocationId { get; set; }

    [Description("Clear departure metadata when re-promoting or returning an evicted NPC.")]
    [JsonPropertyName("clearDeparture")]
    public bool? ClearDeparture { get; set; }
}

/// <summary>
/// Patch or bootstrap a character's ruleset-specific systemStats (partial merge).
/// </summary>
public class SystemStatsChange : WorldChange
{
    [Description("ID of the character to update.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Ruleset stats to merge. Include $system discriminator (dnd5e, pf2e).")]
    [JsonPropertyName("systemStats")]
    public SystemExtension SystemStats { get; set; } = null!;
}

public class KnowledgeUpdate : WorldChange
{
    [Description("ID of the character whose memory is updating.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("The topic/entity the memory is about (e.g. 'The Rusty Tavern', 'Mayor Bob').")]
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = null!;

    [Description("The details of the memory. Write how the character understands it now.")]
    [JsonPropertyName("details")]
    public string Details { get; set; } = null!;

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

    [Description("Entity IDs this memory relates to (characters, items, locations). REQUIRED when this memory is tied to an event: include all involved entities except the remembering character. Enables surface-by-association in future scenes.")]
    [JsonPropertyName("relatedEntityIds")]
    public List<string>? RelatedEntityIds { get; set; }

    [Description("Ground-truth event ID(s) this memory derives from — provide a client-set 'eventId' on this event change, or a prior event's ID. REQUIRED when this memory is event-sourced. Lets later checks compare belief vs. what actually happened (misremembering, rumor drift).")]
    [JsonPropertyName("sourceEventIds")]
    public List<string>? SourceEventIds { get; set; }

    [Description("Deliberate (explicit player act — marking a map, memorizing a fact — locks in high salience) or Passive (ambient, decays naturally, default).")]
    [JsonPropertyName("recordingMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordingMode? RecordingMode { get; set; }
}

/// <summary>
/// Seed a new plot thread — a DM-facing narrative arc that ticks forward whether or not players engage.
/// Unlike Quests (player-facing objectives), PlotThreads are world-state scaffolding: mysteries, conspiracies,
/// slow-burn conflicts, rising threats. The simulation engine escalates tension automatically.
/// </summary>
public class PlotClueDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("description")]
    public string Description { get; set; } = null!;

    [JsonPropertyName("involvedEntityIds")]
    public List<string>? InvolvedEntityIds { get; set; }
}

/// <summary>
/// Update a plot thread's state, tension, notes, resolution condition, or involved entities.
/// Use to manually escalate/de-escalate tension, shift state, or append new foreshadowing.
/// </summary>
public class PlotThreadProgress : WorldChange
{
    [Description("ID of the plot thread to update (e.g. 'plot-threads/guild-infiltration').")]
    [JsonPropertyName("plotThreadId")]
    public string PlotThreadId { get; set; } = null!;

    [Description("New state override. Omit to keep current state.")]
    [JsonPropertyName("newState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlotThreadState? NewState { get; set; }

    [Description("Delta to apply to tension level (-100 to +100). Positive = more urgent. Omit to leave unchanged.")]
    [JsonPropertyName("tensionDelta")]
    public int? TensionDelta { get; set; }

    [Description("Day when Climax state was entered (stamped once, used for auto-resolution timeout). Omit to leave unchanged.")]
    [JsonPropertyName("climaxEnteredDay")]
    public int? ClimaxEnteredDay { get; set; }

    [Description("Replace or set the resolution condition.")]
    [JsonPropertyName("resolutionCondition")]
    public string? ResolutionCondition { get; set; }

    [Description("Append a new foreshadowing hook string to the existing list.")]
    [JsonPropertyName("addForeshadowingHook")]
    public string? AddForeshadowingHook { get; set; }

    [Description("Add an entity ID to InvolvedEntityIds (character, faction, location, or item).")]
    [JsonPropertyName("addInvolvedEntityId")]
    public string? AddInvolvedEntityId { get; set; }

    [Description("Remove an entity ID from InvolvedEntityIds.")]
    [JsonPropertyName("removeInvolvedEntityId")]
    public string? RemoveInvolvedEntityId { get; set; }

    [Description("Add a new clue to the thread's clue chain.")]
    [JsonPropertyName("addClue")]
    public PlotClueDto? AddClue { get; set; }

    [Description("Narrative note about this progress step (stored in DmNotes).")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }
}

/// <summary>
/// Mark a clue in a plot thread as discovered by the party.
/// This resets the staleness timer (prevents the 'no engagement' pressure) and logs the event.
/// Pair with EventOccurred to record the narrative moment of discovery.
/// </summary>
public class PlotThreadClueDiscovered : WorldChange
{
    [Description("ID of the plot thread containing the clue (e.g. 'plot-threads/guild-infiltration').")]
    [JsonPropertyName("plotThreadId")]
    public string PlotThreadId { get; set; } = null!;

    [Description("ID of the specific clue that was discovered (matches PlotClue.Id).")]
    [JsonPropertyName("clueId")]
    public string ClueId { get; set; } = null!;

    [Description("Character IDs who discovered or witnessed the clue.")]
    [JsonPropertyName("discoveredByCharacterIds")]
    public List<string> DiscoveredByCharacterIds { get; set; } = [];

    [Description("Optional narrative note about how the clue was found.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }
}

/// <summary>
/// Spend or recover a resource pool (spell slot, focus point, action point, etc.).
/// Used to track spellcasting, ability uses, and other per-session resource expenditure.
/// Positive delta = recovery (long rest, short rest, end of encounter).
/// Negative delta = expenditure (spell cast, ability used).
/// </summary>
public class ResourceChange : WorldChange
{
    [Description("Character ID whose resource is changing (e.g. 'chars/wizard-1').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Pool name (e.g., 'spell_slots_3', 'font_of_magic', 'focus_points', 'action_points', 'gold' [dnd5e/pf2e currency]). Must match an existing pool in character.systemStats.resourcePools.")]
    [JsonPropertyName("poolName")]
    public string PoolName { get; set; } = null!;

    [Description("Delta to apply to the pool (-3 = spend 3 points, +1 = recover 1 point). Cannot exceed pool max.")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; }

    [Description("Optional reason for the change (e.g., 'Cast Fireball', 'Long rest recovery', 'Exhausted pool').")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [Description("Spell template name when spending spell_slots_* (e.g. 'fireball'). Enables slot-level validation via the spell registry (get_rules_reference kind:'spells').")]
    [JsonPropertyName("spellName")]
    public string? SpellName { get; set; }

    [Description("Simulation-internal: campaign day this recovery is attributed to. When set, stamps ResourcePool.LastRecoveredDay for per-pool idempotency (used by rest/daily recovery paths). Omit for manual LLM resource spends/grants.")]
    [JsonPropertyName("recoveredOnDay")]
    public int? RecoveredOnDay { get; set; }
}

/// <summary>
/// Simulation-internal: marks that pool recovery for a completed rest has been applied.
/// Emitted by ResourceRecoveryRule during advance_world; not intended for LLM commit use.
/// </summary>
public class RestRecoveryAck : WorldChange
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [JsonPropertyName("restDay")]
    public int RestDay { get; set; }

    [JsonPropertyName("restSequence")]
    public int RestSequence { get; set; }
}

/// <summary>
/// Indicates whether a memory/event commit is a passive absorption or deliberate act of recording.
/// Passive: ambient, incidental exposure (overhearing, wandering past something). Decays naturally.
/// Deliberate: explicit player act (marking a map, writing a name down, memorizing a fact). Locked at high salience/importance, skips heuristic inference.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecordingMode
{
    Passive,
    Deliberate
}

/// <summary>
/// Entity kind for <see cref="ArchiveEntityChange"/>. Deliberately excludes Character —
/// the Character model has no IsArchived field, so characters cannot be archived this way
/// (see the tool-usage audit, C1). Attempting Character fails with an explanatory message.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchivableEntityType
{
    Character,
    Location,
    Item,
    Faction,
    Quest,
    Creature,
    Spell,
    Feat,
    Rumor,
    PlotThread
}

/// <summary>
/// Soft-archives (or restores) an entity created via world_build, hiding it from
/// default search/scene/list results without deleting it. This is the play-LLM-reachable
/// counterpart to the internal IsArchived flag most entity upserts already accept — see C1 in
/// the tool-usage audit for why this was previously undiscoverable during play.
/// </summary>
public class ArchiveEntityChange : WorldChange
{
    [Description("The kind of entity being archived/restored. Character is not supported — see this $type's schema description.")]
    [JsonPropertyName("entityType")]
    public ArchivableEntityType EntityType { get; set; }

    [Description("The ID of the entity to archive or restore (e.g. 'quests/stop-nightshade').")]
    [JsonPropertyName("entityId")]
    public string EntityId { get; set; } = null!;

    [Description("true to archive (hide from default results, soft-delete), false to restore visibility. Defaults to true.")]
    [JsonPropertyName("archived")]
    public bool Archived { get; set; } = true;
}

/// <summary>
/// Updates campaign-level meta as part of normal play — currently the narrative focus tags.
/// Lets the DM shift the campaign's center of gravity (dungeon crawl → political thriller)
/// via take_turn without a dedicated management tool.
/// </summary>
public class CampaignUpdateChange : WorldChange
{
    [Description("Full replacement list of narrative focus tags (e.g. ['political intrigue', 'court politics']). Pass every tag you want retained — this replaces the whole list, it does not append.")]
    [JsonPropertyName("narrativeFocus")]
    public List<string>? NarrativeFocus { get; set; }
}

/// <summary>
/// Equip a carried item into one of its EquipZones. Layering is enforced by EquipSlotRules: items
/// in different EquipLayers on the same zone coexist (e.g. an enchanted robe worn over chainmail),
/// while items in the same zone+layer conflict (two breastplates, or a two-handed weapon vs a shield).
/// An item's optional StackGroup carves out an independent sub-slot within a zone+layer, so
/// differently-StackGrouped items (or an ungrouped item vs. a StackGrouped one) coexist there too —
/// this is what lets modular armor parts (left/right pauldron) share Torso/Armor without conflicting.
/// Equipping also runs EquipSlotRules.FindTagIncompatibilities: RequiresEquippedTags/
/// IncompatibleWithEquippedTags are zone/layer-independent prerequisite/incompatibility declarations
/// that always hard-fail (never auto-resolved by ReplaceConflicts, unlike slot conflicts).
/// </summary>
public class ItemEquip : WorldChange
{
    [Description("ID of the character equipping the item.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the item to equip. Must already be carried by (HolderId == ) characterId and have EquipZones/EquipLayer set via world_build.")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;

    [Description("If true, auto-unequips whatever conflicts (same zone+layer, or the off-hand item for a two-handed weapon) to make room. If false/omitted, equipping HARD-FAILS when conflicts exist and lists them — never silently swaps gear.")]
    [JsonPropertyName("replaceConflicts")]
    public bool ReplaceConflicts { get; set; }
}

/// <summary>Unequip a currently equipped item. The item remains carried (HolderId unchanged).</summary>
public class ItemUnequip : WorldChange
{
    [Description("ID of the character unequipping the item.")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("ID of the item to unequip. Must currently be equipped and carried by characterId.")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;
}

/// <summary>
/// Spend or restore charges/doses on a limited-use item (water gourd, healing ointment, reagent vial).
/// Lazy-initializes CurrentCharges to MaxCharges on first use. Hard-fails on insufficient charges —
/// same precedent as the 'resource' commit's currency hard-fail, no silent clamping on spends.
/// Restores (positive delta) clamp to MaxCharges. Reaching 0 charges just logs — the LLM decides via
/// a follow-up commit (item_update/archive_entity/item_transfer) whether the empty container becomes debris.
/// </summary>
public class ItemUse : WorldChange
{
    [Description("ID of the item being used. Must have MaxCharges set (via world_build).")]
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;

    [Description("Charge delta. Negative spends a dose/charge (default -1); positive restores/refills (e.g. refilling a water gourd).")]
    [JsonPropertyName("delta")]
    public int Delta { get; set; } = -1;

    [Description("Optional narrative reason (e.g. 'drank a dose of healing ointment').")]
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Simulation-internal: marks that an ambient item's expiry pressure has been surfaced to the DM-LLM.
/// Emitted by AmbientItemDecayRule during advance_world; not intended for LLM commit use. Idempotent —
/// the item's fate is still decided by the LLM via a follow-up archive_entity/item_transfer/item_update.
/// </summary>
public class ItemPersistenceSurfaced : WorldChange
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = null!;
}

/// <summary>
/// Simulation-internal: adjusts character memory saliencies and urgencies, or evicts memories entirely.
/// Emitted by MemorySalienceDecayRule; applied by MemoryDecayHandler.
/// Maps memory entry key to (newSalience, newUrgency, evict) tuples for batch memory mutation.
/// </summary>
public class MemoryDecay : WorldChange
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    [Description("Map from memory entry key to (newSalience, newUrgency, evict). Null field = no change for that aspect.")]
    [JsonPropertyName("entryChanges")]
    public Dictionary<string, (float? NewSalience, float? NewUrgency, bool Evict)> EntryChanges { get; set; } = [];
}

/// <summary>
/// Transitions a WorldEvent's status or stamps LastTriggeredDay for recurring events.
/// Can be emitted by WorldEventRule during simulation or committed by the DM for freeform narrative resolutions.
/// </summary>
public class WorldEventStatusChange : WorldChange
{
    [JsonPropertyName("worldEventId")]
    public string WorldEventId { get; set; } = null!;

    [Description("New status for the event (Pending/Triggered/Prevented/Resolved). Null = don't change status (used for recurring TimeBased firing).")]
    [JsonPropertyName("newStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorldEventStatus? NewStatus { get; set; }

    [Description("For recurring TimeBased events, stamps the day this cycle fired to prevent double-firing on multi-day skips.")]
    [JsonPropertyName("lastTriggeredDay")]
    public int? LastTriggeredDay { get; set; }

    [Description("Optional narrative note appended to the event's DmNotes.")]
    [JsonPropertyName("narrativeNote")]
    public string? NarrativeNote { get; set; }
}
