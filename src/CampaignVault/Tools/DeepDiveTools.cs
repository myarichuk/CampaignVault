using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class DeepDiveTools : CampaignToolBase, IMcpServerTool
{
    private readonly ExplorationTools _exploration;

    public DeepDiveTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ExplorationTools exploration,
        ILogger<DeepDiveTools>? logger = null)
        : base(repository, keys, logger)
    {
        _exploration = exploration;
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"ENTITY DEEP DIVE: Fetch ONE entity in full detail by its exact ID — the entity type is inferred from the ID prefix:
- 'chars/…' → full NPC context (psychology, social, needs, behavior synthesis, held items, recent interactions)
- 'locations/…' → full scene (present NPCs, items, climate, local rumors, pressures; pass partyPresent:true when the party is physically there)
- 'factions/…' → full faction document (stances, influence, territory)
- 'quests/…' → full quest document (objectives, deadlines, rewards)
- 'items/…' → full item document (including persistent ItemDetails: scratches, secret compartments, damage/wear)
- 'plot-threads/…' → full plot thread (clues, foreshadowing, DM notes); pass the literal id 'plot-threads' to list all active threads
Use search_world first when you only know a name, not the ID. To bundle a full-detail fetch WITH a mutation in one round-trip, use take_turn's fullDetailCharacterId/fullDetailLocationId instead. Requires campaignName.")]
    public async Task<ToolResult<object>> GetEntity(
        [Description("Exact entity ID with type prefix, e.g. 'chars/valen', 'locations/rusty-nail', 'quests/rats_01', 'factions/thieves-guild', 'items/battle-worn-sword', 'plot-threads/guild-infiltration' — or the literal 'plot-threads' to list all active threads.")]
        string entityId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Locations only: set true if the party is physically entering or spending time at the location (prevents transient-NPC cleanup).")]
        bool partyPresent = false)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return await ToolArgumentErrors.Missing<object>(
                "entityId",
                "Pass an exact entity ID with its type prefix (chars/, locations/, factions/, quests/, items/, plot-threads/). Use search_world to find IDs.",
                toolName: "get_entity");
        }

        var id = entityId.Trim();

        if (id.Equals("plot-threads", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await ListPlotThreads(campaignName));
        }

        if (id.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await _exploration.GetNpcContext(id, campaignName));
        }

        if (id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await _exploration.GetScene(id, campaignName, partyPresent));
        }

        if (id.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await GetFactionContext(id, campaignName));
        }

        if (id.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await GetQuestDetails(id, campaignName));
        }

        if (id.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await GetItem(id, campaignName));
        }

        if (id.StartsWith("plot-threads/", StringComparison.OrdinalIgnoreCase))
        {
            return Box(await GetPlotThread(id, campaignName));
        }

        return new ToolResult<object>(false, Error: ToolErrors.InvalidArgument,
            Summary: $"Unrecognized entity ID prefix in '{id}'. Supported prefixes: chars/, locations/, factions/, quests/, items/, plot-threads/ (or literal 'plot-threads' to list threads). For rumors, lore, or name-based lookup use search_world.");
    }

    private static ToolResult<object> Box<T>(ToolResult<T> r) =>
        new(r.Success, r.Data, r.Summary, r.Error, r.WorldPressure, r.RetryExample);

    internal Task<ToolResult<Faction>> GetFactionContext(
        [Description("Exact faction ID e.g. 'factions/thieves-guild' (use search_world first if unsure).")]
        string factionId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var faction = await _repository.GetFactionAsync(session, factionId, effective);
            if (faction == null)
            {
                var suggestions = await _repository.SuggestFactionsAsync(session, factionId, effective);
                var hint = suggestions.Any()
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"))
                    : "";
                return new ToolResult<Faction>(false, Error: "NotFound",
                    Summary: $"Faction '{factionId}' not found.{hint} Use the exact ID from search_world.");
            }

            return new ToolResult<Faction>(true, faction,
                $"Full faction context for {faction.Name} (campaign: {effective}).");
        }, saveChanges: false);
    }

    internal Task<ToolResult<IReadOnlyList<PlotThread>>> ListPlotThreads(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var threads = await _repository.GetActivePlotThreadsAsync(session, effective);
            return new ToolResult<IReadOnlyList<PlotThread>>(
                true,
                threads,
                $"{threads.Count} active plot thread(s) in campaign '{effective}'.");
        }, saveChanges: false);
    }

    internal Task<ToolResult<PlotThread>> GetPlotThread(
        [Description("Exact plot thread ID e.g. 'plot-threads/guild-infiltration'.")]
        string plotThreadId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var thread = await _repository.GetPlotThreadAsync(session, plotThreadId, effective);
            if (thread == null)
                return new ToolResult<PlotThread>(false, Error: "NotFound",
                    Summary: $"PlotThread '{plotThreadId}' not found. Pass 'plot-threads' to get_entity to list available threads.");

            var discoveredClues = thread.Clues.Count(c => c.IsDiscovered);
            return new ToolResult<PlotThread>(true, thread,
                $"Plot thread '{thread.Title}': {thread.State}, tension {thread.TensionLevel}/100, {discoveredClues}/{thread.Clues.Count} clues discovered.");
        }, saveChanges: false);
    }

    internal Task<ToolResult<Quest>> GetQuestDetails(
        [Description("Exact quest ID e.g. 'quests/rats_01'.")]
        string questId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var quest = await _repository.GetQuestAsync(session, questId, effective);
            if (quest == null)
            {
                var suggestions = await _repository.SuggestQuestsAsync(session, questId, effective);
                var hint = suggestions.Any()
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Title})"))
                    : "";
                return new ToolResult<Quest>(false, Error: "NotFound", Summary: $"Quest '{questId}' not found.{hint}");
            }

            return new ToolResult<Quest>(true, quest, $"Quest details for '{quest.Title}' (campaign: {effective}).");
        }, saveChanges: false);
    }

    internal Task<ToolResult<Item>> GetItem(
        [Description("Exact item ID e.g. 'items/battle-worn-sword' (use search_world first if unsure).")]
        string itemId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var item = await _repository.GetItemAsync(session, itemId, effective);
            if (item == null)
            {
                var suggestions = await _repository.SuggestItemsAsync(session, itemId, effective);
                var hint = suggestions.Any()
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"))
                    : "";
                return new ToolResult<Item>(false, Error: "NotFound", Summary: $"Item '{itemId}' not found.{hint}");
            }

            var activeCount = item.ItemDetails.Count(d => !d.IsRetired);
            var retiredCount = item.ItemDetails.Count(d => d.IsRetired);
            return new ToolResult<Item>(true, item,
                $"Item '{item.Name}' (campaign: {effective}), {activeCount} active detail(s)" +
                (retiredCount > 0 ? $", {retiredCount} retired." : "."));
        }, saveChanges: false);
    }
}