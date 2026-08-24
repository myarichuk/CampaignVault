using System.Text.Json;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// LEGACY FACADE: This class is preserved strictly to keep the extensive test suite compiling 
/// and running without requiring thousands of lines of refactoring. 
/// Do NOT add new tools here. Add them to the domain-specific tool classes (e.g. ExplorationTools).
/// The MCP Server registers the domain tool classes directly via reflection.
/// </summary>
public class CampaignTools(
    ExplorationTools exploration,
    MutationTools mutation,
    DeepDiveTools deepDive,
    WorldBuilderTools worldBuilder,
    CombatTools combat,
    CampaignManagementTools management,
    MetaTools meta)
{
    /// <summary>Default slug when tests omit campaignName on this legacy facade (not used by MCP server tools).</summary>
    public const string TestDefaultCampaignSlug = "test-campaign";

    private static string ResolveCampaign(string? campaignName) =>
        string.IsNullOrWhiteSpace(campaignName) ? TestDefaultCampaignSlug : campaignName;

    // --- Exploration ---
    public Task<ToolResult<WorldStateView>>
        GetWorldState(string? partyLocationId = null, string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetWorldState(ResolveCampaign(campaignName), partyLocationId);

    public Task<ToolResult<SceneView>> GetScene(string locationId, bool partyPresent = false,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetScene(locationId, ResolveCampaign(campaignName), partyPresent);

    public Task<ToolResult<NpcContextView>> GetNpcContext(string characterId,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetNpcContext(characterId, ResolveCampaign(campaignName));

    public Task<ToolResult<List<PartyMemberView>>> GetParty(string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetParty(ResolveCampaign(campaignName));

    public Task<ToolResult<UnifiedSearchResult>> SearchWorld(string query,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.SearchWorld(query, ResolveCampaign(campaignName));

    public Task<ToolResult<IEnumerable<Event>>>
        RecallHistory(string query, int limit = 5, string? campaignName = TestDefaultCampaignSlug,
            string? locationId = null, string? involvedCharacterId = null) =>
        exploration.RecallHistory(ResolveCampaign(campaignName), query, limit, locationId, involvedCharacterId);

    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetNpcNeeds(characterId, ResolveCampaign(campaignName));

    // --- Mutation ---
    // Routes through the production take_turn pipeline (the only mutation path) and adapts the
    // TurnResult back to the CommitResult shape the historical test suite asserts on.
    internal async Task<ToolResult<CommitResult>> Commit(WorldChange[]? changes, string? narrative = null,
        string? campaignName = TestDefaultCampaignSlug)
    {
        if (changes is null || changes.Length == 0)
        {
            return await ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "take_turn");
        }

        var turn = await mutation.TakeTurn(
            new TakeTurnRequest { Changes = changes, Narrative = narrative, AutoRefreshInvolved = false },
            ResolveCampaign(campaignName));

        CommitResult? data = null;
        if (turn.Data is { } turnResult)
        {
            data = new CommitResult
            {
                Success = turn.Success,
                ChangesProcessed = turnResult.ChangesProcessed,
                Summary = turnResult.Summary,
                InvolvedEntities = turnResult.InvolvedEntities,
                EntityCollisions = turnResult.EntityCollisions,
                NarrativeReminder = turnResult.NarrativeReminder,
                RateLimitTokensRemaining = turnResult.RateLimitTokensRemaining,
            };
        }

        return new ToolResult<CommitResult>(turn.Success, data, turn.Summary, turn.Error, turn.WorldPressure, turn.RetryExample);
    }

    internal Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative,
        string? campaignName = TestDefaultCampaignSlug)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            return Task.FromResult(new ToolResult<CommitResult>(
                false, Error: "InvalidArgument",
                Summary: "Invalid changes format. Expected JSON array of world changes."));
        }

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(changesJson);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult<CommitResult>(
                false, Error: "InvalidArgument",
                Summary: $"Failed to parse changes JSON: {ex.Message}"));
        }

        if (!CommitChangesParser.TryParse(json, out var parsed, out _))
        {
            return Task.FromResult(new ToolResult<CommitResult>(
                false, Error: "InvalidArgument",
                Summary: "Invalid changes format. Expected JSON array of world changes."));
        }

        return Commit(parsed, narrative, campaignName);
    }

    public Task<ToolResult<TurnResult>> TakeTurn(TakeTurnRequest request,
        string? campaignName = TestDefaultCampaignSlug) =>
        mutation.TakeTurn(request, ResolveCampaign(campaignName));

    public Task<ToolResult<AdvanceResult>> AdvanceWorld(int days, int resultingHour, string narrative,
        string? campaignName = TestDefaultCampaignSlug, int? hours = null) =>
        mutation.AdvanceWorld(narrative, ResolveCampaign(campaignName), days, resultingHour, hours);

    // --- Deep Dives ---
    public Task<ToolResult<Faction>> GetFactionContext(string factionId,
        string? campaignName = TestDefaultCampaignSlug) =>
        deepDive.GetFactionContext(factionId, ResolveCampaign(campaignName));

    public Task<ToolResult<QuestDetailView>> GetQuestDetails(string questId, string? campaignName = TestDefaultCampaignSlug) =>
        deepDive.GetQuestDetails(questId, ResolveCampaign(campaignName));

    // --- World Builder ---
    public Task<ToolResult<CharacterDetailView>> UpsertCharacter(CharacterUpsertRequest character,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertCharacter(character, ResolveCampaign(campaignName));

    public Task<ToolResult<Location>> UpsertLocation(LocationUpsertRequest location,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertLocation(location, ResolveCampaign(campaignName));

    public Task<ToolResult<Lore>> UpsertLore(LoreUpsertRequest lore, string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertLore(lore, ResolveCampaign(campaignName));

    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.DefineNeedDescriptor(needName, descriptor, ResolveCampaign(campaignName));

    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.GetNeedDescriptors(ResolveCampaign(campaignName));

    // --- Combat ---
    public Task<ToolResult<CombatEncounterView>> StartCombat(string locationId, string[] participantIds,
        string? campaignName = TestDefaultCampaignSlug) =>
        combat.StartCombat(locationId, participantIds?.ToList() ?? [], ResolveCampaign(campaignName));

    public Task<ToolResult<CombatEncounterView>>
        NextTurn(string? expectedActiveTurnId = null, string? campaignName = TestDefaultCampaignSlug) =>
        combat.NextTurn(ResolveCampaign(campaignName), expectedActiveTurnId);

    public Task<ToolResult<CombatEncounterView>> EndCombat(string? campaignName = TestDefaultCampaignSlug) =>
        combat.EndCombat(ResolveCampaign(campaignName));

    // --- Campaign Management ---
    public Task<ToolResult<CampaignConfig>> GetConfig(string campaignName) =>
        management.GetConfig(campaignName);

    public Task<ToolResult<CampaignConfig>> SetActiveSystem(string activeSystem,
        Dictionary<string, string>? systemOptions = null, string? campaignName = TestDefaultCampaignSlug) =>
        management.SetActiveSystem(activeSystem, ResolveCampaign(campaignName), systemOptions);

    public Task<ToolResult<Campaign>> CreateCampaign(string name, string system, string? displayName = null) =>
        management.CreateCampaign(name, system, displayName);

    public Task<ToolResult<List<Campaign>>> ListCampaigns() => management.ListCampaigns();
    public Task<ToolResult<CampaignContextView>> GetCurrentCampaign(string campaignName) =>
        management.GetCurrentCampaign(campaignName);

    public Task<ToolResult<SystemHandbookResponse>> GetSystemHandbook(string campaignName) =>
        management.GetSystemHandbook(campaignName);

    public Task<ToolResult<SpellListResponse>> GetSpells(
        string @class,
        string campaignName,
        int? level = null,
        int offset = 0,
        int? limit = null) =>
        management.GetSpells(@class, campaignName, level, offset, limit);

    // --- Meta ---
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(string? category = null) =>
        meta.ListTools(category);

    public Task<ToolResult<string>> GetHelp(string? topic = null) => meta.GetHelp(topic);
}