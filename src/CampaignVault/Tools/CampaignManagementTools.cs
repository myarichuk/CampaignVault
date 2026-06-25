using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

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
    [Description(@"RULES CONFIG TOOL: Get campaign configuration (ruleset and house-rule options).")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
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

Example: set_active_system(RulesetSystem.Pathfinder2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")]
        RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")]
        Dictionary<string, string>? systemOptions = null,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
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
    [Description(@"CAMPAIGN TOOL: Creates a new campaign with a slug and initial ruleset (locked immediately).
Selects the new campaign for this MCP session when Mcp-Session-Id or MCP_SESSION_ID is available; otherwise pass campaignName on subsequent calls.
Slugs are canonicalized ('Dragon Heist' → dragon-heist).
Available Systems: Dnd5e, Pathfinder2e, Fallout2d20, Narrative

Example: create_campaign(""dragon-heist"", RulesetSystem.Dnd5e, ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string name,
        [Description("Initial ruleset system. This will be locked.")]
        RulesetSystem initialSystem,
        [Description("Optional human-friendly display name.")]
        string? displayName = null)
    {
        string normalized;
        try
        {
            normalized = CampaignSlug.Canonicalize(name);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new ToolResult<Campaign>(false, Error: ToolErrors.InvalidArgument, Summary: ex.Message));
        }

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

            return new ToolResult<Campaign>(true, campaign,
                $"Campaign '{normalized}' created and locked to {initialSystem}. Pass campaignName='{normalized}' on subsequent calls.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Lists all existing campaigns (campaigns/*/meta documents only).
Useful for discovering existing worlds. Pass the slug as campaignName on subsequent calls.")]
    public Task<ToolResult<List<Campaign>>> ListCampaigns()
    {
        return ExecuteAsync(async session =>
        {
            var campaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/") && c.Id.EndsWith("/meta"))
                .ToListAsync();

            return new ToolResult<List<Campaign>>(true, campaigns, $"Found {campaigns.Count} campaign(s).");
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("REMOVED: Use explicit campaignName on every tool call. This tool no longer exists.")]
    public Task<ToolResult<SelectCampaignResult>> SelectCampaign(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string campaignName,
        [Description("Deprecated.")]
        bool confirmCreate = false)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return ToolArgumentErrors.Missing<SelectCampaignResult>(
                "campaignName",
                "Call list_campaigns first, then pass campaignName as a slug.",
                toolName: "select_campaign");
        }

        if (!CampaignSlug.TryCanonicalize(campaignName, out var normalized))
        {
            return Task.FromResult(new ToolResult<SelectCampaignResult>(false,
                Error: ToolErrors.InvalidArgument,
                Summary: "Provide a valid, non-empty campaign slug."));
        }

        return Task.FromResult(new ToolResult<SelectCampaignResult>(
            false,
            Error: "Removed",
            Summary: "select_campaign has been removed. Pass campaignName explicitly on every tool call."));
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"CAMPAIGN DISCOVERABILITY: Returns the currently active campaign context (meta + posture: party roster, entry hint, last event).
Use this if you are unsure which campaign you are currently in or if you need to know the active ruleset system (e.g., Dnd5e, Pf2e) before using ruleset_actions in combat.
Pass campaignName explicitly when MCP_STATELESS=1 or when no Mcp-Session-Id / MCP_SESSION_ID is available.")]
    public Task<ToolResult<CampaignContextView>> GetCurrentCampaign(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        var explicitName = CampaignSlug.Canonicalize(campaignName);
        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(explicitName);
            var campaign = await session.LoadAsync<Campaign>(campaignId);
            if (campaign == null)
            {
                return new ToolResult<CampaignContextView>(false, Error: "NotFound",
                    Summary:
                    $"Campaign '{explicitName}' meta document not found. The campaign might not be initialized yet.");
            }

            var posture = await CampaignPostureBuilder.BuildAsync(session, _repository, _keys, explicitName,
                isNewCampaign: false);
            return new ToolResult<CampaignContextView>(
                true,
                new CampaignContextView(campaign, posture),
                $"Campaign context for '{explicitName}' ({posture.EntryHint}).");
        }, saveChanges: false);
    }

    private async Task<IReadOnlyList<CampaignSuggestion>> BuildSuggestionsAsync(
        IAsyncDocumentSession session,
        string requestedSlug,
        IReadOnlyList<Campaign> campaigns)
    {
        var baseSuggestions = CampaignSlugMatcher.FindSuggestions(
            requestedSlug,
            campaigns,
            c => new CampaignSuggestion(
                c.Name,
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Name : c.DisplayName,
                c.System,
                0,
                null));

        if (baseSuggestions.Count == 0)
        {
            return baseSuggestions;
        }

        var enriched = new List<CampaignSuggestion>(baseSuggestions.Count);
        foreach (var suggestion in baseSuggestions)
        {
            var pcCount = await session.Query<Character>()
                .Where(c => c.CampaignName == suggestion.Slug && c.IsPc)
                .CountAsync();

            var events = await _repository.QueryEventsAsync(session, null, null, 1, suggestion.Slug);
            enriched.Add(suggestion with
            {
                PcCount = pcCount,
                LastEventSummary = events.FirstOrDefault()?.Summary
            });
        }

        return enriched;
    }
}