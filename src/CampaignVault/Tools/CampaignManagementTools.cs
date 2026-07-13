using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CampaignManagementTools(
    CampaignRepository repository,
    CampaignDocumentKeys keys,
    SpellDefinitionProvider spellProvider,
    ClassDefinitionProvider classProvider,
    RaceDefinitionProvider raceProvider,
    BackgroundDefinitionProvider backgroundProvider,
    FeatDefinitionProvider featProvider,
    ConditionDefinitionProvider conditionProvider,
    CreatureDefinitionProvider creatureProvider)
    : CampaignToolBase(repository, keys)
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
Available Systems: Dnd5e, Pathfinder2e, Narrative

Example: set_active_system(RulesetSystem.Pathfinder2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")]
        RulesetSystem activeSystem,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Optional dictionary of system options and house rules.")]
        Dictionary<string, string>? systemOptions = null)
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
Pass campaignName on subsequent calls.
Slugs are canonicalized ('Dragon Heist' → dragon-heist).
Available Systems: Dnd5e, Pathfinder2e, Narrative

Example: create_campaign(""dragon-heist"", RulesetSystem.Dnd5e, ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string name,
        [Description("Initial ruleset system. This will be locked.")]
        RulesetSystem initialSystem,
        [Description("Optional human-friendly display name.")]
        string? displayName = null,
        [Description("Optional free-text tags describing the kind(s) of story this campaign tells (e.g. ['political intrigue'], ['dungeon crawl'], ['horror investigation']). Steers how the LLM should judge event importance on commit — see the Narrative Focus section in get_help. Can be set or changed later via set_narrative_focus.")]
        List<string>? narrativeFocus = null)
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
            if (narrativeFocus is { Count: > 0 })
            {
                campaign.NarrativeFocus = narrativeFocus;
            }

            return new ToolResult<Campaign>(true, campaign,
                $"Campaign '{normalized}' created and locked to {initialSystem}. Pass campaignName='{normalized}' on subsequent calls.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Set or update the campaign's narrative focus tags (e.g. ['political intrigue'], ['dungeon crawl'], ['horror investigation']).
Campaigns evolve — a dungeon crawl can turn into a political thriller. Call this any time the story's center of gravity shifts.
Replaces the full tag list; pass all tags you want retained, not just the new ones.
See the Narrative Focus section in get_help for how this steers event-importance judgment on commit.")]
    public Task<ToolResult<List<string>>> SetNarrativeFocus(
        [Description("Full replacement list of narrative focus tags (e.g. ['political intrigue', 'court politics']).")]
        List<string> tags,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var campaignId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(campaignId);
            if (campaign == null)
            {
                return new ToolResult<List<string>>(false, Error: "NotFound",
                    Summary: $"Campaign '{effective}' meta document not found. The campaign might not be initialized yet.");
            }

            campaign.NarrativeFocus = tags ?? [];
            return new ToolResult<List<string>>(true, campaign.NarrativeFocus,
                $"Narrative focus for '{effective}' set to: {string.Join(", ", campaign.NarrativeFocus)}.");
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
    [Description(
        @"RULESET DISCOVERY: Returns available classes, races, backgrounds, feats, conditions, skills, and creatures for the campaign's active ruleset.
Homebrew YAML on disk (RulesetData/{system}/) and feats authored via upsert_feat appear automatically alongside embedded SRD/ORC defaults.
Call before upsert_character or when applying typed conditionName values. For spells, see notes → get_spells. For creatures, use query_creatures for paginated SRD + homebrew merged results.
Creature data is available for dnd5e and pf2e. Skills are freeform ability checks with no fixed reference list for either system.")]
    public Task<ToolResult<SystemHandbookResponse>> GetSystemHandbook(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var homebrewFeats = await _repository.GetCustomFeatsForSystemAsync(session, config.ActiveSystem, effective);
            var handbook = SystemHandbookBuilder.Build(
                config.ActiveSystem,
                classProvider,
                raceProvider,
                backgroundProvider,
                featProvider,
                conditionProvider,
                creatureProvider,
                homebrewFeats);

            return new ToolResult<SystemHandbookResponse>(
                true,
                handbook,
                $"System handbook for {handbook.System} ({handbook.Classes.Count} classes, " +
                $"{handbook.Conditions.Count} conditions, campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"SPELL DISCOVERY: Returns spell metadata (level, concentration, casting time) for the campaign's active ruleset.
Filter by class and optional spell level. Results are paginated (default 40 per page) — use offset/limit or level filter for large lists.
Homebrew spells authored via upsert_spell appear automatically (override SRD by name); RulesetData/{system}/spells/ on disk also appears.
Use spell names from this tool in resource commits (spellName field) for slot validation.")]
    public Task<ToolResult<SpellListResponse>> GetSpells(
        [Description("Class name for list filtering (e.g. 'Wizard', 'Cleric'). Required.")]
        string @class,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Optional spell level filter (0 = cantrip). Strongly recommended — full class lists are large.")]
        int? level = null,
        [Description("Pagination offset (default 0). Use response.pagination.hasMore and hint for next page.")]
        int offset = 0,
        [Description("Page size (default 40, max 100).")]
        int? limit = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var system = config.ActiveSystem;
            var homebrew = await _repository.GetCustomSpellsForSystemAsync(session, system, effective);
            var page = SpellQueryBuilder.QueryPage(
                spellProvider, system, @class, classProvider, level, offset, limit, homebrew);

            var response = SpellQueryBuilder.ToResponse(system, @class, level, page, ToSpellSummary);

            return new ToolResult<SpellListResponse>(
                true,
                response,
                response.Hint);
        }, saveChanges: false);
    }

    private static SpellSummaryView ToSpellSummary(SpellDefinition spell) =>
        new()
        {
            Name = spell.Name,
            Level = spell.Level ?? 0,
            Concentration = spell.Concentration ?? false,
            CastingTime = spell.CastingTime
        };

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"CREATURE DISCOVERY: Returns creature stat-block templates (both SRD reference data and campaign homebrew).
Paginated results merge SRD and homebrew creatures (homebrew overrides SRD by name). Use for NPC/monster stat-block lookup.
Note: This surfaces stat-block *templates* (reusable reference data), not live instances. Use upsert_character to place a creature instance in the world.")]
    public Task<ToolResult<CreatureListResponse>> QueryCreatures(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Optional creature name substring filter.")]
        string? nameQuery = null,
        [Description("Optional minimum level filter (for numeric sorting/range).")]
        int? levelMin = null,
        [Description("Optional maximum level filter (for numeric sorting/range).")]
        int? levelMax = null,
        [Description("Pagination offset (default 0). Use response.pagination.hasMore and hint for next page.")]
        int offset = 0,
        [Description("Page size (default 40, max 100).")]
        int? limit = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var system = config.ActiveSystem;
            var page = await CreatureQueryBuilder.QueryPageAsync(
                session, _repository, creatureProvider, system, effective,
                nameQuery, levelMin, levelMax, offset, limit);

            var response = CreatureQueryBuilder.ToResponse(system, page);

            return new ToolResult<CreatureListResponse>(
                true,
                response,
                response.Hint);
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"CAMPAIGN DISCOVERABILITY: Returns campaign context (meta + posture: party roster, entry hint, last event).
Use this if you need the active ruleset system (e.g., Dnd5e, Pathfinder2e) before using ruleset_actions in combat.
Requires campaignName.")]
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
}