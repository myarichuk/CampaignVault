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
[JsonDerivedType(typeof(EventOccurred), "event")]
[JsonDerivedType(typeof(RumorEvolves), "rumor")]
[JsonDerivedType(typeof(RelationshipChange), "relationship")]
[JsonDerivedType(typeof(NeedChange), "need")]
[JsonDerivedType(typeof(AttributeChange), "attribute")]
[JsonDerivedType(typeof(MoodChange), "mood")]
[JsonDerivedType(typeof(ActivityChange), "activity")]
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

/// <summary>Add a named status/condition to a character (e.g. Poisoned, Frightened, Blessed, OnFire). Does not remove existing statuses.</summary>
public class StatusChange : WorldChange
{
    [Description("ID of the character receiving the status (e.g. 'characters/grog').")]
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [Description("Name of the status condition to add. Use clear narrative names like 'Poisoned', 'Frightened', 'Blessed', 'Grappled', 'OnFire'.")]
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

    [Description("Name of the need being adjusted. The system is intentionally open: use 'hunger', 'thirst', 'tiredness', 'arousal', or invent narrative-appropriate ones like 'wanderlust', 'homesickness', 'vengeance', 'duty'.")]
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
