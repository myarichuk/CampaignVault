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
    [Description(@"RULES CONFIG TOOL: Get campaign configuration (ruleset and house-rule options). Uses session-selected campaign unless campaignName is passed.")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
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

Example: set_active_system(RulesetSystem.Pathfinder2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")]
        RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")]
        Dictionary<string, string>? systemOptions = null,
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
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

            _currentCampaign.SetCurrent(normalized);

            return new ToolResult<Campaign>(true, campaign,
                $"Campaign '{normalized}' created and locked to {initialSystem}. Now selected as current.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Lists all existing campaigns (campaigns/*/meta documents only).
Useful for discovering existing worlds before calling select_campaign.")]
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
    [Description(@"CAMPAIGN TOOL: Selects a campaign as the current one for this MCP session.
Requires Mcp-Session-Id (HTTP) or MCP_SESSION_ID (stdio/local). Subsequent tool calls in the same session may omit campaignName.
When MCP_STATELESS=1 or no session is available, pass campaignName on every tool call instead.

If the slug does not exist exactly, returns fuzzy suggestions (did you mean sword-coast?) without selecting.
Pass confirmCreate=true only when intentionally creating a new minimal campaign (prefer create_campaign for full setup).

Example: select_campaign(""dragon-heist"")")]
    public Task<ToolResult<SelectCampaignResult>> SelectCampaign(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string campaignName,
        [Description("When true and no exact/fuzzy match exists, creates a minimal D&D 5e campaign and selects it.")]
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

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);

            if (existing != null)
            {
                _currentCampaign.SetCurrent(normalized);
                var posture = await CampaignPostureBuilder.BuildAsync(session, _repository, _keys, normalized,
                    isNewCampaign: false);
                return new ToolResult<SelectCampaignResult>(
                    true,
                    new SelectCampaignResult(normalized, posture),
                    $"Campaign '{normalized}' is now selected ({posture.EntryHint}).");
            }

            var allCampaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/") && c.Id.EndsWith("/meta"))
                .ToListAsync();

            var suggestions = await BuildSuggestionsAsync(session, normalized, allCampaigns);
            if (suggestions.Count > 0)
            {
                var names = string.Join(", ", suggestions.Select(s => $"'{s.Slug}'"));
                return new ToolResult<SelectCampaignResult>(
                    false,
                    new SelectCampaignResult(normalized, Suggestions: suggestions),
                    Error: ToolErrors.SlugAmbiguous,
                    Summary:
                    $"No exact match for '{normalized}'. Did you mean: {names}? Call select_campaign again with the exact slug.");
            }

            if (!confirmCreate)
            {
                return new ToolResult<SelectCampaignResult>(
                    false,
                    new SelectCampaignResult(normalized),
                    Error: ToolErrors.SlugNotFound,
                    Summary:
                    $"Campaign '{normalized}' not found. Call list_campaigns, pick an existing slug, use create_campaign for a new world, or pass confirmCreate=true to create a minimal campaign.");
            }

            await GetOrCreateCampaignMetaAsync(session, normalized, RulesetSystem.Dnd5e, forceLock: false);
            _currentCampaign.SetCurrent(normalized);
            var newPosture = await CampaignPostureBuilder.BuildAsync(session, _repository, _keys, normalized,
                isNewCampaign: true);
            return new ToolResult<SelectCampaignResult>(
                true,
                new SelectCampaignResult(normalized, newPosture),
                $"Campaign '{normalized}' created (minimal, D&D 5e default) and selected ({newPosture.EntryHint}).");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"CAMPAIGN DISCOVERABILITY: Returns the currently active campaign context (meta + posture: party roster, entry hint, last event).
Use this if you are unsure which campaign you are currently in or if you need to know the active ruleset system (e.g., Dnd5e, Pf2e) before using ruleset_actions in combat.
Pass campaignName explicitly when MCP_STATELESS=1 or when no Mcp-Session-Id / MCP_SESSION_ID is available.")]
    public Task<ToolResult<CampaignContextView>> GetCurrentCampaign(
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
        string? campaignName = null)
    {
        if (!string.IsNullOrWhiteSpace(campaignName))
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

        if (!_currentCampaign.HasSelection)
        {
            return Task.FromResult(new ToolResult<CampaignContextView>(
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
                return new ToolResult<CampaignContextView>(false, Error: "NotFound",
                    Summary:
                    $"Campaign '{effective}' meta document not found. The campaign might not be initialized yet.");
            }

            var posture = await CampaignPostureBuilder.BuildAsync(session, _repository, _keys, effective,
                isNewCampaign: false);
            return new ToolResult<CampaignContextView>(
                true,
                new CampaignContextView(campaign, posture),
                $"Currently selected campaign: {effective} ({posture.EntryHint}).");
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