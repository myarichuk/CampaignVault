using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
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

    public Task<ToolResult<List<Character>>> GetParty(string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetParty(ResolveCampaign(campaignName));

    public Task<ToolResult<UnifiedSearchResult>> SearchWorld(string query,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.SearchWorld(query, ResolveCampaign(campaignName));

    public Task<ToolResult<IEnumerable<Event>>>
        RecallHistory(string query, int limit = 5, string? campaignName = TestDefaultCampaignSlug) =>
        exploration.RecallHistory(query, ResolveCampaign(campaignName), limit);

    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId,
        string? campaignName = TestDefaultCampaignSlug) =>
        exploration.GetNpcNeeds(characterId, ResolveCampaign(campaignName));

    // --- Mutation ---
    public Task<ToolResult<CommitResult>> Commit(JsonElement? changes = null, string? narrative = null,
        string? campaignName = TestDefaultCampaignSlug) =>
        mutation.Commit(ResolveCampaign(campaignName), changes, narrative);

    public Task<ToolResult<CommitResult>> Commit(WorldChange[]? changes, string? narrative = null,
        string? campaignName = TestDefaultCampaignSlug) =>
        mutation.Commit(changes, narrative, ResolveCampaign(campaignName));

    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative,
        string? campaignName = TestDefaultCampaignSlug) =>
        mutation.Commit(changesJson, narrative, ResolveCampaign(campaignName));

    public Task<ToolResult<AdvanceResult>> AdvanceWorld(int days, TimeOfDay timeOfDay, string narrative,
        string? campaignName = TestDefaultCampaignSlug) =>
        mutation.AdvanceWorld(days, timeOfDay, narrative, ResolveCampaign(campaignName));

    // --- Deep Dives ---
    public Task<ToolResult<Faction>> GetFactionContext(string factionId,
        string? campaignName = TestDefaultCampaignSlug) =>
        deepDive.GetFactionContext(factionId, ResolveCampaign(campaignName));

    public Task<ToolResult<Quest>> GetQuestDetails(string questId, string? campaignName = TestDefaultCampaignSlug) =>
        deepDive.GetQuestDetails(questId, ResolveCampaign(campaignName));

    // --- World Builder ---
    public Task<ToolResult<Character>> UpsertCharacter(Character character,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertCharacter(character, ResolveCampaign(campaignName));

    public Task<ToolResult<Location>> UpsertLocation(Location location,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertLocation(location, ResolveCampaign(campaignName));

    public Task<ToolResult<Lore>> UpsertLore(Lore lore, string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.UpsertLore(lore, ResolveCampaign(campaignName));

    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor,
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.DefineNeedDescriptor(needName, descriptor, ResolveCampaign(campaignName));

    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        string? campaignName = TestDefaultCampaignSlug) =>
        worldBuilder.GetNeedDescriptors(ResolveCampaign(campaignName));

    // --- Combat ---
    public Task<ToolResult<CombatEncounter>> StartCombat(string locationId, string[] participantIds,
        string? campaignName = TestDefaultCampaignSlug) =>
        combat.StartCombat(locationId, participantIds, ResolveCampaign(campaignName));

    public Task<ToolResult<CombatEncounter>>
        NextTurn(string? expectedActiveTurnId = null, string? campaignName = TestDefaultCampaignSlug) =>
        combat.NextTurn(ResolveCampaign(campaignName), expectedActiveTurnId);

    public Task<ToolResult<CombatEncounter>> EndCombat(string? campaignName = TestDefaultCampaignSlug) =>
        combat.EndCombat(ResolveCampaign(campaignName));

    // --- Campaign Management ---
    public Task<ToolResult<CampaignConfig>> GetConfig(string campaignName) =>
        management.GetConfig(campaignName);

    public Task<ToolResult<CampaignConfig>> SetActiveSystem(RulesetSystem activeSystem,
        Dictionary<string, string>? systemOptions = null, string? campaignName = TestDefaultCampaignSlug) =>
        management.SetActiveSystem(activeSystem, ResolveCampaign(campaignName), systemOptions);

    public Task<ToolResult<Campaign>> CreateCampaign(string name, RulesetSystem system, string? displayName = null) =>
        management.CreateCampaign(name, system, displayName);

    public Task<ToolResult<List<Campaign>>> ListCampaigns() => management.ListCampaigns();
    public Task<ToolResult<CampaignContextView>> GetCurrentCampaign(string campaignName) =>
        management.GetCurrentCampaign(campaignName);

    // --- Meta ---
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(string? category = null) =>
        meta.ListTools(category);

    public Task<ToolResult<string>> GetHelp() => meta.GetHelp();
}