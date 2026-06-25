using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class DeepDiveTools : CampaignToolBase
{
    public DeepDiveTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys)
        : base(repository, keys)
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
}