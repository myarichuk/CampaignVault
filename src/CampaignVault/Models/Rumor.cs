namespace CampaignVault.Models;

public class Rumor
{
    public string Id { get; set; } = default!;
    
    public string RegionLocationId { get; set; } = default!;
    
    public string Subject { get; set; } = default!;
    
    public string CurrentText { get; set; } = default!;
    
    public RumorState State { get; set; } = RumorState.Nascent;
    
    /// <summary>
    /// Narrative metadata about how truthful the rumor is considered to be.
    /// Exposed to LLMs and DMs for roleplay and decision making.
    /// </summary>
    public RumorTruth TruthValue { get; set; } = RumorTruth.True;
    
    public int DayCreated { get; set; }
    
    public int LastStateChangeDay { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Rumors are campaign-specific and should not be global.)
    /// </summary>
    public string? CampaignName { get; set; }
}

public enum RumorState
{
    Nascent,
    Spreading,
    Peak,
    Fading,
    Resolved,
    Forgotten
}

/// <summary>
/// How truthful a rumor is considered (narrative metadata for DMs and LLMs).
/// Replaces the previous free-text string for better discoverability and type safety.
/// </summary>
public enum RumorTruth
{
    True,
    False,
    PartiallyTrue,
    Misleading,
    Unknown
}
