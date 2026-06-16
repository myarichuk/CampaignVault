using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;

namespace CampaignVault.Tools;

[McpServerToolType]
public class DeepDiveTools : CampaignToolBase
{
    public DeepDiveTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign)
        : base(repository, currentCampaign ?? new CurrentCampaignContext(), keys ?? new CampaignDocumentKeys())
    {
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DEEP DIVE TOOL: Returns the full Faction document (stances, influence, territory, leaders, metadata, DM notes) for a known faction ID. Use this (instead of guessing from get_scene summaries) when you need to roleplay faction reactions, declare war, expand territory, or check player rep impact. Campaign-scoped.")]
    public Task<ToolResult<Faction>> GetFactionContext(
        [Description("Exact faction ID e.g. 'factions/thieves-guild' (use fuzzy search or get_scene first if unsure).")] string factionId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var faction = await _repository.GetFactionAsync(session, factionId, effective);
            if (faction == null)
            {
                var suggestions = await _repository.SuggestFactionsAsync(session, factionId, effective);
                var hint = suggestions.Any() 
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"))
                    : "";
                return new ToolResult<Faction>(false, Error: "NotFound", Summary: $"Faction '{factionId}' not found.{hint} Use exact ID from get_scene or search.");
            }
            return new ToolResult<Faction>(true, faction, $"Full faction context for {faction.Name} (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DEEP DIVE TOOL: Returns the full Quest document (all objectives with states, deadlines, rewards, giver, related locations/factions, DM notes, urgency). Use when get_scene shows an ActiveQuestSummary and you need to advance/fail specific objectives or check stakes. Supports per-objective deadlines from Phase 7.3.")]
    public Task<ToolResult<Quest>> GetQuestDetails(
        [Description("Exact quest ID e.g. 'quests/rats_01'.")] string questId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var quest = await _repository.GetQuestAsync(session, questId, effective);
            if (quest == null)
            {
                var suggestions = await _repository.SuggestQuestsAsync(session, questId, effective);
                var hint = suggestions.Any() ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Title})")) : "";
                return new ToolResult<Quest>(false, Error: "NotFound", Summary: $"Quest '{questId}' not found.{hint}");
            }
            return new ToolResult<Quest>(true, quest, $"Quest details for '{quest.Title}' (campaign: {effective}).");
        }, saveChanges: false);
    }
}
