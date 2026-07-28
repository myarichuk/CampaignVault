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
        "Array of world changes to commit (optional). REQUIRED: Each item MUST include '$type' field with the change type name (e.g. 'event', 'hp', 'engagement_relation', 'activity', 'status', etc.). Without $type, deserialization will fail. Omit entirely for pure queries.")]
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

    [Description(
        "Include full Party member summaries (all player characters' summary state) in response (default false).")]
    [JsonPropertyName("includeParty")]
    public bool IncludeParty { get; set; } = false;

    [Description(
        "Include WorldState (rumors, quests, factions, time) in response (default false). Set to true when you need overall campaign state context.")]
    [JsonPropertyName("includeWorldState")]
    public bool IncludeWorldState { get; set; } = false;

    [Description(
        "Location ID anchoring WorldState scoping (only used if IncludeWorldState=true). Omit to skip location-based scoping — rumors/quests/factions are then filtered only by party affiliations, and PartyLocation comes back null.")]
    [JsonPropertyName("partyLocationId")]
    public string? PartyLocationId { get; set; }

    [Description(
        "NPC ID to fetch in full detail (NpcContextView with all relationships, history, needs) instead of summary. Use sparingly; only one full detail per call.")]
    [JsonPropertyName("fullDetailCharacterId")]
    public string? FullDetailCharacterId { get; set; }

    [Description(
        "Location ID to fetch in full detail (SceneView with all details) instead of summary. Use sparingly; only one full detail per call.")]
    [JsonPropertyName("fullDetailLocationId")]
    public string? FullDetailLocationId { get; set; }
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

    [Description("Full party member summaries (if includeParty=true); otherwise null.")]
    public List<PartyMemberView>? Party { get; set; }

    [Description("World state including rumors, active quests, faction standings, and campaign time (if includeWorldState=true); otherwise null.")]
    public WorldStateView? WorldState { get; set; }

    [Description("Full NPC context view for the requested NPC (if fullDetailCharacterId was provided); includes all relationships, history, and behavior synthesis. Otherwise null.")]
    public NpcContextView? FullNpcContext { get; set; }

    [Description("Full scene view for the requested location (if fullDetailLocationId was provided); includes all NPCs, items, and environmental details. Otherwise null.")]
    public SceneView? FullScene { get; set; }

    [Description("Non-fatal problems encountered while assembling this response (failed refreshes, missing entities, world-state errors). Null when every requested section succeeded. Check this whenever an expected section came back null.")]
    public List<string>? Warnings { get; set; }
}
