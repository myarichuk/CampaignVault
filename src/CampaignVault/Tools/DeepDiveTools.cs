using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class DeepDiveTools : CampaignToolBase, IMcpServerTool
{
    public DeepDiveTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ILogger<DeepDiveTools>? logger = null)
        : base(repository, keys, logger)
    {
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "DEEP DIVE TOOL: Returns the full Faction document (stances, influence, territory, leaders, metadata). Requires campaignName.")]
    public Task<ToolResult<Faction>> GetFactionContext(
        [Description("Exact faction ID e.g. 'factions/thieves-guild' (use fuzzy search or get_scene first if unsure).")]
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
                    Summary: $"Faction '{factionId}' not found.{hint} Use exact ID from get_scene or search.");
            }

            return new ToolResult<Faction>(true, faction,
                $"Full faction context for {faction.Name} (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "PLOT THREADS: Returns all active/escalating/climax plot threads for the campaign — DM-facing narrative arcs. " +
        "Includes tension level, discovered clue count, and resolution condition. Requires campaignName.")]
    public Task<ToolResult<IReadOnlyList<PlotThread>>> ListPlotThreads(
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

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "PLOT THREAD DEEP DIVE: Returns the full PlotThread document — all clues, foreshadowing hooks, involved entities, and DM notes. Requires campaignName.")]
    public Task<ToolResult<PlotThread>> GetPlotThread(
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
                    Summary: $"PlotThread '{plotThreadId}' not found. Use list_plot_threads to see available IDs.");

            var discoveredClues = thread.Clues.Count(c => c.IsDiscovered);
            return new ToolResult<PlotThread>(true, thread,
                $"Plot thread '{thread.Title}': {thread.State}, tension {thread.TensionLevel}/100, {discoveredClues}/{thread.Clues.Count} clues discovered.");
        }, saveChanges: false);
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "DEEP DIVE TOOL: Returns the full Quest document (objectives, deadlines, rewards, giver). Requires campaignName.")]
    public Task<ToolResult<Quest>> GetQuestDetails(
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

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "DEEP DIVE TOOL: Returns the full Item document by exact ID — including all persistent ItemDetails " +
        "(scratches, stains, secret compartments, damage/wear) with their ids, useful before retiring a detail " +
        "via item_update's retireItemDetailId, or before reviewing an item's full history. For fuzzy/semantic " +
        "item lookup by name, use search_world instead. Requires campaignName.")]
    public Task<ToolResult<Item>> GetItem(
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