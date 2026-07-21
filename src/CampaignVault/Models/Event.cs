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
    
    [JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [JsonIgnore]
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
    /// Narrative importance of this event to the current campaign's story (see Campaign.NarrativeFocus).
    /// Drives importance-ranked retrieval budgets (ambient context, NPC context, recall) instead of pure recency.
    /// </summary>
    public MemoryImportance Importance { get; set; } = MemoryImportance.Important;

    /// <summary>
    /// Cosine similarity (0-1) against the most similar recent event at commit time, as computed by
    /// EventNoveltyAdvisor. Persisted for future retrieval tie-breaking; null when novelty scoring was
    /// skipped (bookkeeping categories, or no embedding available).
    /// </summary>
    public double? NoveltyScore { get; set; }

    /// <summary>Whether this event is tied to <paramref name="locationId"/> via any spatial anchor field.</summary>
    public bool TouchesLocation(string locationId)
    {
        if (string.IsNullOrEmpty(locationId))
        {
            return false;
        }

        if (string.Equals(LocationId, locationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(RelatedEntityId, locationId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (RelatedLocationIds?.Contains(locationId, StringComparer.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return Involved?.Contains(locationId, StringComparer.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Resolves the primary location ID for consequence templates: prefers explicit <see cref="LocationId"/>,
    /// then legacy <see cref="RelatedEntityId"/>, then spillover/involved location IDs.
    /// </summary>
    public string? ResolvePrimaryLocationId()
    {
        if (IsLocationId(LocationId))
        {
            return LocationId;
        }

        if (IsLocationId(RelatedEntityId))
        {
            return RelatedEntityId;
        }

        var spillover = RelatedLocationIds?.FirstOrDefault(IsLocationId);
        if (spillover != null)
        {
            return spillover;
        }

        return Involved?.FirstOrDefault(IsLocationId);
    }

    private static bool IsLocationId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/log (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Events are campaign-specific and should not be global.)
    /// </summary>
    public string? CampaignName { get; set; }
}
