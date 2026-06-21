using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CampaignVault.Tools;

[McpServerToolType]
public class WorldBuilderTools : CampaignToolBase
{
    public WorldBuilderTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign)
        : base(repository, currentCampaign, keys)
    {
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

STRONGLY encouraged to populate:
- Mind.Wants, Mind.Fears, Mind.Knows
- Detailed backstory in Notes
- Schedule + Routines + StateModifiers
- Mind.NeedDescriptors (human-readable explanations for any custom needs)
- Equipment via Items (set HolderId to the character)

This is the best opportunity to create deep, simulatable NPCs.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The full Character object to create or replace. Strongly typed.")] Character character,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertCharacterAsync(s, character, effective);
            return new ToolResult<Character>(true, character, $"Character upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Register a new location on the world map. For first-time setup only.

Define hierarchical locations with exits, parent relationships, and rich metadata.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The full Location object to create or replace. Strongly typed.")] Location location,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLocationAsync(s, location, effective);
            return new ToolResult<Location>(true, location, $"Location upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")] Lore lore,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLoreAsync(s, lore, effective);
            return new ToolResult<Lore>(true, lore, $"Lore upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Define or update a descriptor for a need type for the current/selected campaign. Automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list defined ones for the campaign. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
        {
            return Task.FromResult(new ToolResult<string>(false, Error: "BadRequest", Summary: "needName and descriptor are required."));
        }

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            await _repository.SetNeedDescriptorAsync(session, needName, descriptor, effective);
            return new ToolResult<string>(true, $"Descriptor for '{needName}' stored for campaign '{effective}'.", $"Descriptor persisted for campaign '{effective}'.");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Lists all defined need descriptors for the current (or specified) campaign.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var descriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            return new ToolResult<Dictionary<string, string>>(true, descriptors, 
                descriptors.Count > 0 
                    ? $"Retrieved {descriptors.Count} need descriptors for campaign '{effective}'."
                    : $"No need descriptors defined yet for campaign '{effective}'.");
        }, saveChanges: false);
    }
}
