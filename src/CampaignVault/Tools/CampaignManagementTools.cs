using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CampaignManagementTools(
    CampaignRepository repository,
    ICurrentCampaignContext currentCampaign,
    CampaignDocumentKeys keys)
    : CampaignToolBase(repository, currentCampaign, keys)
{
    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Get the current campaign configuration.
Returns the ruleset and system-specific options (e.g., house rules). Respects the currently selected campaign.")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description("Optional campaign name. Falls back to the currently selected campaign (via select_campaign).")]
        string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            return new ToolResult<CampaignConfig>(true, config, $"Campaign configuration retrieved for '{effective}'.");
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Set the active ruleset system for a campaign.
Respects lock-in (cannot change system once locked). Use this to define house rules or system options.
Available Systems: Dnd5e, Pathfinder2e, Fallout2d20, Narrative

Example: set_active_system(RulesetSystem.Pf2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")]
        RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")]
        Dictionary<string, string>? systemOptions = null,
        [Description("Optional campaign name. Falls back to currently selected.")]
        string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var campaign = await GetOrCreateCampaignMetaAsync(session, effective, activeSystem, forceLock: false);

            if (campaign.IsSystemLocked && campaign.System != activeSystem)
            {
                return new ToolResult<CampaignConfig>(
                    false,
                    Error: "SystemLocked",
                    Summary:
                    $"The ruleset for campaign '{effective}' is locked to {campaign.System}. Cannot change to {activeSystem}.");
            }

            var config = await _repository.GetCampaignConfigAsync(session, effective);
            config.ActiveSystem = activeSystem;
            config.SystemOptions = systemOptions ?? [];
            await _repository.UpsertCampaignConfigAsync(session, config, effective);

            if (!campaign.IsSystemLocked)
            {
                campaign.System = activeSystem;
                campaign.IsSystemLocked = true;
            }

            return new ToolResult<CampaignConfig>(true, config,
                $"Active ruleset for '{effective}' set to '{activeSystem}' (locked).");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Creates a new campaign with a name and initial ruleset.
The ruleset is immediately locked for this campaign, preventing accidental system changes later.
Automatically selects the newly created campaign as the current one.
Available Systems: Dnd5e, Pathfinder2e, Fallout2d20, Narrative

Example: create_campaign(""dragonheist"", RulesetSystem.Dnd5e, ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description("Unique name/slug for the campaign (e.g. 'dragonheist', 'curse-of-strahd').")]
        string name,
        [Description("Initial ruleset system. This will be locked.")]
        RulesetSystem initialSystem,
        [Description("Optional human-friendly display name.")]
        string? displayName = null)
    {
        var normalized = name.Trim().ToLowerInvariant();

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);
            if (existing != null)
            {
                return new ToolResult<Campaign>(false, Error: "AlreadyExists",
                    Summary: $"Campaign '{normalized}' already exists.");
            }

            var campaign =
                await GetOrCreateCampaignMetaAsync(session, normalized, initialSystem, displayName, forceLock: true);

            // Select it immediately for convenience
            _currentCampaign.SetCurrent(normalized);

            return new ToolResult<Campaign>(true, campaign,
                $"Campaign '{normalized}' created and locked to {initialSystem}. Now selected as current.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Lists all existing campaigns in the database.
Useful for discovering existing worlds to join before calling select_campaign.")]
    public Task<ToolResult<List<Campaign>>> ListCampaigns()
    {
        return ExecuteAsync(async session =>
        {
            // Query all Campaign documents (they live under campaigns/*/meta)
            var campaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/"))
                .ToListAsync();

            return new ToolResult<List<Campaign>>(true, campaigns, $"Found {campaigns.Count} campaign(s).");
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Selects a campaign as the current one for this session.
Most tools will use this campaign context automatically, meaning you don't need to specify 'campaignName' on subsequent tool calls.

Example: select_campaign(""dragonheist"")")]
    public Task<ToolResult<string>> SelectCampaign(
        [Description("Name of the campaign to select.")]
        string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return ToolArgumentErrors.Missing<string>(
                "campaignName",
                "Call list_campaigns first, then pass campaignName as a slug.",
                toolName: "select_campaign");
        }

        var normalized = campaignName.Trim().ToLowerInvariant();

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);

            if (existing == null)
            {
                // Auto-create a minimal campaign entry so lock-in and per-campaign state can work
                await GetOrCreateCampaignMetaAsync(session, normalized, RulesetSystem.Dnd5e, forceLock: false);
                _currentCampaign.SetCurrent(normalized);
                return new ToolResult<string>(true, normalized,
                    $"Campaign '{normalized}' selected (new minimal campaign created with D&D 5e as default system).");
            }

            _currentCampaign.SetCurrent(normalized);
            return new ToolResult<string>(true, normalized, $"Campaign '{normalized}' is now selected as current.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"CAMPAIGN DISCOVERABILITY: Returns the currently active campaign context (name, lock-in status, and active ruleset).
Use this if you are unsure which campaign you are currently in or if you need to know the active ruleset system (e.g., Dnd5e, Pf2e) before using ruleset_actions in combat.
Pass campaignName explicitly when MCP_STATELESS=1 or when select_campaign was called in a prior request without a session.")]
    public Task<ToolResult<Campaign>> GetCurrentCampaign(
        [Description("Optional campaign name. Falls back to the currently selected campaign (via select_campaign).")]
        string? campaignName = null)
    {
        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            var explicitName = campaignName.Trim().ToLowerInvariant();
            return ExecuteAsync(async session =>
            {
                var campaignId = _keys.Meta(explicitName);
                var campaign = await session.LoadAsync<Campaign>(campaignId);
                if (campaign == null)
                {
                    return new ToolResult<Campaign>(false, Error: "NotFound",
                        Summary:
                        $"Campaign '{explicitName}' meta document not found. The campaign might not be initialized yet.");
                }

                return new ToolResult<Campaign>(true, campaign, $"Campaign context for '{explicitName}'.");
            }, saveChanges: false);
        }

        if (!_currentCampaign.HasSelection)
        {
            return Task.FromResult(new ToolResult<Campaign>(
                false,
                Error: ToolErrors.NoCampaignSelected,
                Summary: NoCampaignSelectedSummary));
        }

        var effective = _currentCampaign.CurrentCampaignName;
        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(campaignId);
            if (campaign == null)
            {
                return new ToolResult<Campaign>(false, Error: "NotFound",
                    Summary:
                    $"Campaign '{effective}' meta document not found. The campaign might not be initialized yet.");
            }

            return new ToolResult<Campaign>(true, campaign, $"Currently selected campaign: {effective}");
        }, saveChanges: false);
    }
}