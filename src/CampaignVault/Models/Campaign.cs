using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// First-class Campaign document. Replaces the implicit single "campaign" assumption.
/// Stored as "campaigns/{Name}/meta" (or similar namespaced key).
/// Each campaign has its own locked ruleset system, options, time, combat, etc.
/// This enables future multi-campaign support while providing "lock in" for the campaign type today.
/// </summary>
public class Campaign
{
    /// <summary>
    /// Campaign identifier / slug (URL-safe, lowercase recommended).
    /// Used as the key segment in document IDs (e.g. "campaigns/dragonheist/config").
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Human-friendly display name.
    /// </summary>
    public string DisplayName { get; set; } = default!;

    /// <summary>
    /// The (locked) TTRPG ruleset for this specific campaign.
    /// Once initialized/locked, changing this is prevented (or requires explicit force + audit).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RulesetSystem System { get; set; } = RulesetSystem.Dnd5e;

    /// <summary>
    /// Whether the ruleset system has been locked for this campaign.
    /// Set true on first InitializeCampaign or first SetActiveSystem after creation.
    /// </summary>
    public bool IsSystemLocked { get; set; }

    /// <summary>
    /// Optional house-rule / system-specific options for this campaign.
    /// These remain mutable even after system lock (per current design decision).
    /// </summary>
    public Dictionary<string, string> SystemOptions { get; set; } = [];

    /// <summary>
    /// Creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional free-form metadata (description, DM notes, version, etc.).
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    // Future: per-campaign defaults, player list, etc. can be added here without breaking storage.
}