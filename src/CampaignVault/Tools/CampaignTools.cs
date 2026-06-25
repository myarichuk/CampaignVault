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
    // --- Exploration ---
    public Task<ToolResult<WorldStateView>>
        GetWorldState(string? partyLocationId = null, string? campaignName = null) =>
        exploration.GetWorldState(partyLocationId, campaignName);

    public Task<ToolResult<SceneView>> GetScene(string locationId, bool partyPresent = false,
        string? campaignName = null) => exploration.GetScene(locationId, partyPresent, campaignName);

    public Task<ToolResult<NpcContextView>> GetNpcContext(string characterId, string? campaignName = null) =>
        exploration.GetNpcContext(characterId, campaignName);

    public Task<ToolResult<List<Character>>> GetParty(string? campaignName = null) =>
        exploration.GetParty(campaignName);

    public Task<ToolResult<UnifiedSearchResult>> SearchWorld(string query, string? campaignName = null) =>
        exploration.SearchWorld(query, campaignName);

    public Task<ToolResult<IEnumerable<Event>>>
        RecallHistory(string query, int limit = 5, string? campaignName = null) =>
        exploration.RecallHistory(query, limit, campaignName);

    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId, string? campaignName = null) =>
        exploration.GetNpcNeeds(characterId, campaignName);

    // --- Mutation ---
    public Task<ToolResult<CommitResult>> Commit(JsonElement? changes = null, string? narrative = null,
        string? campaignName = null) => mutation.Commit(changes, narrative, campaignName);

    public Task<ToolResult<CommitResult>> Commit(WorldChange[]? changes, string? narrative = null,
        string? campaignName = null) => mutation.Commit(changes, narrative, campaignName);

    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative, string? campaignName = null) =>
        mutation.Commit(changesJson, narrative, campaignName);

    public Task<ToolResult<AdvanceResult>> AdvanceWorld(int days, TimeOfDay timeOfDay, string narrative,
        string? campaignName = null) => mutation.AdvanceWorld(days, timeOfDay, narrative, campaignName);

    // --- Deep Dives ---
    public Task<ToolResult<Faction>> GetFactionContext(string factionId, string? campaignName = null) =>
        deepDive.GetFactionContext(factionId, campaignName);

    public Task<ToolResult<Quest>> GetQuestDetails(string questId, string? campaignName = null) =>
        deepDive.GetQuestDetails(questId, campaignName);

    // --- World Builder ---
    public Task<ToolResult<Character>> UpsertCharacter(Character character, string? campaignName = null) =>
        worldBuilder.UpsertCharacter(character, campaignName);

    public Task<ToolResult<Location>> UpsertLocation(Location location, string? campaignName = null) =>
        worldBuilder.UpsertLocation(location, campaignName);

    public Task<ToolResult<Lore>> UpsertLore(Lore lore, string? campaignName = null) =>
        worldBuilder.UpsertLore(lore, campaignName);

    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor,
        string? campaignName = null) => worldBuilder.DefineNeedDescriptor(needName, descriptor, campaignName);

    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(string? campaignName = null) =>
        worldBuilder.GetNeedDescriptors(campaignName);

    // --- Combat ---
    public Task<ToolResult<CombatEncounter>> StartCombat(string locationId, string[] participantIds,
        string? campaignName = null) => combat.StartCombat(locationId, participantIds, campaignName);

    public Task<ToolResult<CombatEncounter>>
        NextTurn(string? expectedActiveTurnId = null, string? campaignName = null) =>
        combat.NextTurn(expectedActiveTurnId, campaignName);

    public Task<ToolResult<CombatEncounter>> EndCombat(string? campaignName = null) => combat.EndCombat(campaignName);

    // --- Campaign Management ---
    public Task<ToolResult<CampaignConfig>> GetConfig(string campaignName) =>
        management.GetConfig(campaignName);

    public Task<ToolResult<CampaignConfig>> SetActiveSystem(RulesetSystem activeSystem,
        Dictionary<string, string>? systemOptions = null, string? campaignName = null) =>
        management.SetActiveSystem(activeSystem, systemOptions, campaignName);

    public Task<ToolResult<Campaign>> CreateCampaign(string name, RulesetSystem system, string? displayName = null) =>
        management.CreateCampaign(name, system, displayName);

    public Task<ToolResult<List<Campaign>>> ListCampaigns() => management.ListCampaigns();
    public Task<ToolResult<SelectCampaignResult>> SelectCampaign(string name, bool confirmCreate = false) =>
        management.SelectCampaign(name, confirmCreate);
    public Task<ToolResult<CampaignContextView>> GetCurrentCampaign(string campaignName) =>
        management.GetCurrentCampaign(campaignName);

    // --- Meta ---
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(string? category = null) =>
        meta.ListTools(category);

    public Task<ToolResult<string>> GetHelp() => meta.GetHelp();
}