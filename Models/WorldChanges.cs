using System.Text.Json.Serialization;

namespace CampaignVault.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HpChange), "hp")]
[JsonDerivedType(typeof(ItemTransfer), "item")]
[JsonDerivedType(typeof(StatusChange), "status")]
[JsonDerivedType(typeof(EventOccurred), "event")]
[JsonDerivedType(typeof(RumorEvolves), "rumor")]
[JsonDerivedType(typeof(RelationshipChange), "relationship")]
[JsonDerivedType(typeof(NeedChange), "need")]
[JsonDerivedType(typeof(AttributeChange), "attribute")]
public abstract class WorldChange;

/// <summary>Adjust a character's HP by a positive or negative delta.</summary>
public class HpChange : WorldChange
{
    public string CharacterId { get; set; } = default!;
    public int Delta { get; set; }
}

/// <summary>Move an item to a new holder. ToHolderId can be a character, location, or container item.</summary>
public class ItemTransfer : WorldChange
{
    public string ItemId { get; set; } = default!;
    public string ToHolderId { get; set; } = default!;
}

/// <summary>Add a status condition to a character (e.g. 'Poisoned', 'Frightened').</summary>
public class StatusChange : WorldChange
{
    public string CharacterId { get; set; } = default!;
    public string Status { get; set; } = default!;
}

/// <summary>Log a specific occurrence in the world state. Use 'unresolved' type for plot threads.</summary>
public class EventOccurred : WorldChange
{
    public string Summary { get; set; } = default!;
    public string Type { get; set; } = default!;
    public List<string>? Involved { get; set; }
}

/// <summary>Evolve a rumor's state and optionally update its narrative text.</summary>
public class RumorEvolves : WorldChange
{
    public string RumorId { get; set; } = default!;
    public RumorState NewState { get; set; }
    public string? NewText { get; set; }
}

/// <summary>Shift a relationship score between two characters. Delta range: -100 to 100.</summary>
public class RelationshipChange : WorldChange
{
    public string SourceId { get; set; } = default!;
    public string TargetId { get; set; } = default!;
    public int Delta { get; set; }
    public string Reason { get; set; } = default!;
}

/// <summary>Adjust a need on a character (hunger, thirst, tiredness, arousal). Delta negative = satisfy/reduce need.</summary>
public class NeedChange : WorldChange
{
    public string CharacterId { get; set; } = default!;
    public string Need { get; set; } = default!; // hunger, thirst, tiredness, arousal
    public float Delta { get; set; }
}

/// <summary>Set or adjust an attribute (willpower, temperature, morale). LLM/narrative driven.</summary>
public class AttributeChange : WorldChange
{
    public string CharacterId { get; set; } = default!;
    public string Attribute { get; set; } = default!; // willpower, temperature, morale
    public float Value { get; set; }
}
