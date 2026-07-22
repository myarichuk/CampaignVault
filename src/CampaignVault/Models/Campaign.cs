using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// First-class Campaign document. Replaces the implicit single "campaign" assumption.
/// Stored as "campaigns/{Name}/meta" (or similar namespaced key).
/// Each campaign has its own locked ruleset system, options, time, combat, etc.
/// Each RavenDB instance hosts multiple campaigns; singletons (time, combat, config) are namespaced per slug.
/// </summary>
public class Campaign
{
    /// <summary>
    /// RavenDB document ID (e.g. "campaigns/dragonheist/meta").
    /// Populated by the store on load / after explicit Store with ID.
    /// </summary>
    public string Id { get; set; } = default!;

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

    /// <summary>
    /// Tracks pressures that have been recently surfaced to deduplicate and escalate.
    /// Key is formatted as "{Category}:{EntityId}" (e.g., "NarrativePrompt:locations/rusty-nail").
    /// </summary>
    public Dictionary<string, PressureState> PressureCooldowns { get; set; } = [];

    /// <summary>
    /// Counts commits since one last reported time passage (either crossed a day boundary, or a
    /// change in the batch carried MinutesElapsed). Reset to 0 by StageChangesAsync whenever a commit
    /// does either, and by AdvanceWorldAsync unconditionally (an explicit day-skip always counts as
    /// "time recorded"). Read by TimeStalenessPressureContributor to nudge the DM-LLM to record
    /// narrative time passage once this climbs past CampaignConfig.TimeStalenessNudgeThreshold.
    /// </summary>
    public int CommitsSinceTimeRecorded { get; set; }

    /// <summary>
    /// Tracks NPC initiatives surfaced and consumed on read-side tools.
    /// Key format: initiative:{npcId}:{initiativeKey}
    /// </summary>
    public Dictionary<string, InitiativeSurfacedState> InitiativeSurfaced { get; set; } = [];

    /// <summary>
    /// Free-text tags describing the kind(s) of story this campaign tells (e.g. "political intrigue",
    /// "dungeon crawl", "horror investigation"). No server-side genre->importance matrix — these steer
    /// the LLM's own judgment of event Importance on commit (see DmHelpManual's Narrative Focus section).
    /// Mutable at any time via set_narrative_focus; campaigns can evolve mid-story.
    /// </summary>
    public List<string> NarrativeFocus { get; set; } = [];

    // Future: per-campaign defaults, player list, etc. can be added here without breaking storage.
}