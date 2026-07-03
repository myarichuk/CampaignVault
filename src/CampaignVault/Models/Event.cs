using System.Text.Json.Serialization;

namespace CampaignVault.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventCategory
{
    Unresolved,
    Combat,
    Conversation,
    Discovery,
    Arrival,
    Betrayal,
    SceneCommit,
    Timeskip,
    Simulation,
    Interaction,
    Test,
    Travel,
    SceneInterrupt,
    /// <summary>Transient NPC left a location (engine eviction or explicit departure).</summary>
    Departure
}

public class Event : ICampaignScopedEntity
{
    public string Id { get; set; } = default!;
    
    public float[]? SemanticVector { get; set; }
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => Summary ?? string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int DayLogged { get; set; }
    
    public string? SessionId { get; set; }
    
    public EventCategory Category { get; set; } = EventCategory.Unresolved;
    
    public string Summary { get; set; } = default!;
    
    public IDictionary<string, object>? Details { get; set; }
    
    public List<string> Involved { get; set; } = [];

    /// <summary>Optional relational beat from EventOccurred (e.g. gratitude, gift_received).</summary>
    public string? EmotionalBeat { get; set; }

    /// <summary>Optional related entity ID from EventOccurred (item, character, location).</summary>
    public string? RelatedEntityId { get; set; }

    /// <summary>Primary location where the event occurred.</summary>
    public string? LocationId { get; set; }

    /// <summary>Additional locations touched by a spillover beat (e.g. a bar fight that spills into an alley).</summary>
    public List<string>? RelatedLocationIds { get; set; }

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/log (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Events are campaign-specific and should not be global.)
    /// </summary>
    public string? CampaignName { get; set; }
}
