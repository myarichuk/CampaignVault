using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Request to take_turn: optional mutations + optional refresh/query specifications.
/// Null/empty Changes = pure query; populated Changes = mutation with auto-refresh of touched entities.
/// </summary>
public class TakeTurnRequest
{
    [Description(
        "Array of world changes to commit (optional). Each item must include a '$type' discriminator. Omit entirely for pure queries.")]
    [JsonPropertyName("changes")]
    public WorldChange[]? Changes { get; set; }

    [Description(
        "Narrative summary of what happened. Required if Changes is provided; omit for pure queries.")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }

    [Description(
        "Automatically refresh summary state for entities touched by Changes (default true). Set false for bulk/seeding commits to save bandwidth.")]
    [JsonPropertyName("autoRefreshInvolved")]
    public bool AutoRefreshInvolved { get; set; } = true;

    [Description(
        "Additional NPC IDs to refresh even if not touched by Changes (e.g. to keep other party members' state current).")]
    [JsonPropertyName("extraCharacterIds")]
    public string[]? ExtraCharacterIds { get; set; }

    [Description(
        "Additional location IDs to refresh even if not touched by Changes (e.g. to monitor adjacent rooms).")]
    [JsonPropertyName("extraLocationIds")]
    public string[]? ExtraLocationIds { get; set; }
}

/// <summary>
/// Response from take_turn: mutation outcome + fresh entity state bundled together.
/// Committed=false and ChangesProcessed=0 for pure-query calls; all other fields match Commit's behavior.
/// </summary>
public class TurnResult
{
    [Description("True if a mutation was successfully committed; false if this was a query-only call or commit failed.")]
    public bool Committed { get; set; }

    [Description("Number of WorldChanges processed by the mutation (0 for query-only calls).")]
    public int ChangesProcessed { get; set; }

    [Description("Narrative summary of each change processed.")]
    public List<string> Summary { get; set; } = [];

    [Description("IDs of all entities touched or created (mixed types: chars/, locations/, items/, etc.).")]
    public List<string> InvolvedEntities { get; set; } = [];

    [Description("Entity IDs where a create-style change hit an existing document and was merged instead of creating new.")]
    public List<string> EntityCollisions { get; set; } = [];

    [Description("Optional reminder about the commit outcome (e.g. 'missing narrative event', 'missing PoI detail').")]
    public string? NarrativeReminder { get; set; }

    [Description("Remaining rate-limit tokens for this campaign after this commit.")]
    public int? RateLimitTokensRemaining { get; set; }

    [Description("Bundled fresh NPC summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraCharacterIds. Capped at 6 NPCs.")]
    public List<NpcSummaryView>? Npcs { get; set; }

    [Description("Bundled fresh scene summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraLocationIds. Capped at 3 scenes.")]
    public List<SceneSummaryView>? Scenes { get; set; }

    [Description("Entity IDs that were dropped from Npcs/Scenes due to refresh caps (6 NPCs / 3 scenes). Re-request these explicitly via extraCharacterIds/extraLocationIds if needed.")]
    public List<string>? RefreshTruncatedIds { get; set; }
}
