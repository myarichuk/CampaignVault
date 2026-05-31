using System.ComponentModel;
using System.Text.Json.Serialization;

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
[JsonDerivedType(typeof(RelationshipChange), "relationship")]
[JsonDerivedType(typeof(NeedChange), "need")]
[JsonDerivedType(typeof(AttributeChange), "attribute")]
[JsonDerivedType(typeof(MoodChange), "mood")]
[JsonDerivedType(typeof(ActivityChange), "activity")]
[JsonDerivedType(typeof(RulesetAction), "ruleset_action")]
public abstract class WorldChange;

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

    [Description("Classification of the event. Use 'Unresolved' for dangling plot hooks the party should follow up on. Other good values: 'Combat', 'Conversation', 'Discovery', 'Arrival', 'Betrayal'.")]
    [JsonPropertyName("category")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventCategory Category { get; set; } = EventCategory.Unresolved;

    [Description("Optional list of entity IDs involved (characters, locations, items, etc.). Helps later queries and NPC context.")]
    [JsonPropertyName("involved")]
    public List<string>? Involved { get; set; }
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

/// <summary>Apply a numeric delta to the relationship score between two characters. Range is typically -100 to +100.</summary>
public class RelationshipChange : WorldChange
{
    [Description("ID of the source character whose opinion of the target is changing (e.g. 'characters/elara-voss').")]
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = default!;

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

    [Description("New location ID for the character (e.g. 'locations/rusty-nail'). This must be a valid location. Omit to leave location unchanged.")]
    [JsonPropertyName("newLocationId")]
    public string? NewLocationId { get; set; }

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
/// parameters keys (all optional, resolver-specific):
///   "dc"            – numeric difficulty class (string) for skill/opposed checks
///   "difficulty"    – success count threshold for Fallout 2d20 (default "1")
///   "targetPart"    – BodyPart enum string for hit-location targeting (Fallout 2d20)
///   "advantage"     – "true"/"false" for D&amp;D 5e
///   "item"          – item document ID for UseItem actions
///   "initiativeSkill" – skill name overriding default initiative (PF2e)
///   "targetSkill"   – skill name for the target side of a ContestedCheck
/// </summary>
public class RulesetAction : WorldChange
{
    /// <summary>ID of the acting character (attacker, skill user, healer, etc.).</summary>
    [JsonPropertyName("actorId")]
    public string ActorId { get; set; } = default!;

    /// <summary>
    /// IDs of target characters. Empty for self-only actions.
    /// Multiple targets for AoE spells or group checks.
    /// </summary>
    [JsonPropertyName("targetIds")]
    public List<string> TargetIds { get; set; } = [];

    /// <summary>
    /// Free-text name of the action. Weapon name, spell name, or skill name.
    /// Examples: "longsword", "Fireball", "Athletics", "SmallGuns", "TreatWounds".
    /// NOT an enum — the space is intentionally open-ended.
    /// </summary>
    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = default!;

    /// <summary>What kind of action this is. Must be one of the RulesetActionType enum values.</summary>
    [JsonPropertyName("actionType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RulesetActionType ActionType { get; set; }

    /// <summary>Broad category of the action. Must be one of the ActionCategory enum values.</summary>
    [JsonPropertyName("actionCategory")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionCategory ActionCategory { get; set; }

    /// <summary>
    /// Resolver-specific overrides as string key-value pairs.
    /// See class-level summary for the documented parameter keys.
    /// </summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; set; } = [];
}
