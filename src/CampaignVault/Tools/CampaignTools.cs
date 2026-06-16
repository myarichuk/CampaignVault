using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace CampaignVault.Tools;

/// <summary>
/// LEGACY FACADE: This class is preserved strictly to keep the extensive test suite compiling 
/// and running without requiring thousands of lines of refactoring. 
/// Do NOT add new tools here. Add them to the domain-specific tool classes (e.g. ExplorationTools).
/// The MCP Server registers the domain tool classes directly via reflection.
/// </summary>
public class CampaignTools
{
    private readonly ExplorationTools _exploration;
    private readonly MutationTools _mutation;
    private readonly DeepDiveTools _deepDive;
    private readonly WorldBuilderTools _worldBuilder;
    private readonly CombatTools _combat;
    private readonly CampaignManagementTools _management;
    private readonly MetaTools _meta;

    public CampaignTools(
        CampaignRepository repository,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        IRulesetModuleSelector rulesetSelector,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign,
        IPressureManager? pressureManager = null,
        IPressureOrchestrator? pressureOrchestrator = null)
    {
        // Supply defaults mimicking original behavior
        keys ??= new CampaignDocumentKeys();
        currentCampaign ??= new CurrentCampaignContext();
        pressureManager ??= new PressureManager(keys);
        pressureOrchestrator ??= new PressureOrchestrator(DefaultPressureContributors.All(), pressureManager, rulesetSelector);

        _exploration = new ExplorationTools(repository, behaviorSynthesizer, rulesetSelector, keys, currentCampaign, pressureManager, pressureOrchestrator);
        _mutation = new MutationTools(repository, currentCampaign, keys, pressureManager);
        _deepDive = new DeepDiveTools(repository, keys, currentCampaign);
        _worldBuilder = new WorldBuilderTools(repository, keys, currentCampaign);
        _combat = new CombatTools(repository, currentCampaign, keys, rulesetSelector);
        _management = new CampaignManagementTools(repository, currentCampaign, keys);
        _meta = new MetaTools();
    }

    // --- Exploration ---
    public Task<ToolResult<WorldStateView>> GetWorldState(string? partyLocationId = null, string? campaignName = null) => _exploration.GetWorldState(partyLocationId, campaignName);
    public Task<ToolResult<SceneView>> GetScene(string locationId, bool partyPresent = false, string? campaignName = null) => _exploration.GetScene(locationId, partyPresent, campaignName);
    public Task<ToolResult<NpcContextView>> GetNpcContext(string? characterId = null, string? campaignName = null) => _exploration.GetNpcContext(characterId, campaignName);
    public Task<ToolResult<List<Character>>> GetParty(string? campaignName = null) => _exploration.GetParty(campaignName);
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(string query, string? campaignName = null) => _exploration.SearchWorld(query, campaignName);
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(string query, int limit = 5, string? campaignName = null) => _exploration.RecallHistory(query, limit, campaignName);
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId, string? campaignName = null) => _exploration.GetNpcNeeds(characterId, campaignName);

    // --- Mutation ---
    public Task<ToolResult<CommitResult>> Commit(JsonElement? changes = null, string? narrative = null, string? campaignName = null) => _mutation.Commit(changes, narrative, campaignName);
    public Task<ToolResult<CommitResult>> Commit(WorldChange[]? changes, string? narrative = null, string? campaignName = null) => _mutation.Commit(changes, narrative, campaignName);
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative, string? campaignName = null) => _mutation.Commit(changesJson, narrative, campaignName);
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(int days, TimeOfDay timeOfDay, string narrative, string? campaignName = null) => _mutation.AdvanceWorld(days, timeOfDay, narrative, campaignName);

    // --- Deep Dives ---
    public Task<ToolResult<Faction>> GetFactionContext(string factionId, string? campaignName = null) => _deepDive.GetFactionContext(factionId, campaignName);
    public Task<ToolResult<Quest>> GetQuestDetails(string questId, string? campaignName = null) => _deepDive.GetQuestDetails(questId, campaignName);

    // --- World Builder ---
    public Task<ToolResult<Character>> UpsertCharacter(Character character, string? campaignName = null) => _worldBuilder.UpsertCharacter(character, campaignName);
    public Task<ToolResult<Location>> UpsertLocation(Location location, string? campaignName = null) => _worldBuilder.UpsertLocation(location, campaignName);
    public Task<ToolResult<Lore>> UpsertLore(Lore lore, string? campaignName = null) => _worldBuilder.UpsertLore(lore, campaignName);
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor, string? campaignName = null) => _worldBuilder.DefineNeedDescriptor(needName, descriptor, campaignName);
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(string? campaignName = null) => _worldBuilder.GetNeedDescriptors(campaignName);

    // --- Combat ---
    public Task<ToolResult<CombatEncounter>> StartCombat(string locationId, string[] participantIds, string? campaignName = null) => _combat.StartCombat(locationId, participantIds, campaignName);
    public Task<ToolResult<CombatEncounter>> NextTurn(string? expectedActiveTurnId = null, string? campaignName = null) => _combat.NextTurn(expectedActiveTurnId, campaignName);
    public Task<ToolResult<CombatEncounter>> EndCombat(string? campaignName = null) => _combat.EndCombat(campaignName);

    // --- Campaign Management ---
    public Task<ToolResult<CampaignConfig>> GetConfig(string? campaignName = null) => _management.GetConfig(campaignName);
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(RulesetSystem activeSystem, Dictionary<string, string>? systemOptions = null, string? campaignName = null) => _management.SetActiveSystem(activeSystem, systemOptions, campaignName);
    public Task<ToolResult<Campaign>> CreateCampaign(string name, RulesetSystem system, string? displayName = null) => _management.CreateCampaign(name, system, displayName);
    public Task<ToolResult<List<Campaign>>> ListCampaigns() => _management.ListCampaigns();
    public Task<ToolResult<string>> SelectCampaign(string name) => _management.SelectCampaign(name);
    public Task<ToolResult<Campaign>> GetCurrentCampaign() => _management.GetCurrentCampaign();

    // --- Meta ---
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(string? category = null) => _meta.ListTools(category);
    public Task<ToolResult<string>> GetHelp() => _meta.GetHelp();
}
