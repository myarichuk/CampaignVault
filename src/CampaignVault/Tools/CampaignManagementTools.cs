using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    CreatureDefinitionProvider creatureProvider,
    ProgressionDefinitionProvider progressionProvider,
    ItemDefinitionProvider itemProvider,
    ILogger<CampaignManagementTools>? logger = null)
    : CampaignToolBase(repository, keys, logger), IMcpServerTool
{
    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Get campaign configuration — active ruleset system (Dnd5e/Pathfinder2e/Narrative) and house-rule options. Campaign narrative context (party roster, narrative focus, last event) comes from start_session instead.")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
            return new ToolResult<CampaignConfig>(true, config, $"Campaign configuration retrieved for '{effective}'.");
        }, saveChanges: false);
    }

    internal Task<ToolResult<CampaignConfig>> SetActiveSystem(
        string activeSystem,
        string campaignName,
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

            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
            config.ActiveSystem = activeSystem;

            // Start from the caller-supplied overrides (or whatever's already on file), then fill in
            // any keys plugins declared defaults for via plugin.json campaignOptions — operator/DM
            // values always win, plugin defaults only ever fill gaps, never overwrite.
            config.SystemOptions = systemOptions ?? config.SystemOptions;
            var options = ApplyPluginCampaignOptionDefaults(config);
            await _repository.UpsertCampaignConfigAsync(session, config, effective);

            // Keep meta SystemOptions in sync — take_turn handlers read Campaign.SystemOptions.
            campaign.SystemOptions = options;

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
Available system ids: dnd5e, pf2e, narrative (plugins may add more)

Example: create_campaign(""dragon-heist"", ""dnd5e"", ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description(ToolParameterDescriptions.CampaignSlugRequired)]
        string name,
        [Description("Initial ruleset system. This will be locked.")]
        string initialSystem,
        [Description("Optional human-friendly display name.")]
        string? displayName = null,
        [Description("Optional story tags, e.g. ['political intrigue']. Steers event importance.")]
        List<string>? narrativeFocus = null,
        [Description("Optional era name (default 'Current Era').")]
        string? loreEpoch = null,
        [Description("Optional start year (default 1492).")]
        int? loreYear = null,
        [Description("Optional start month 1-12 (default 1).")]
        int? loreMonth = null,
        [Description("Optional start day 1-30 (default 1).")]
        int? loreDay = null,
        [Description("Optional start hour 0-23 (default 6).")]
        int? loreHour = null)
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
            if (existing is { IsSystemLocked: true })
            {
                return new ToolResult<Campaign>(false, Error: "AlreadyExists",
                    Summary: $"Campaign '{normalized}' already exists.");
            }

            var loreSettings = new CampaignLoreSettings
            {
                Epoch = loreEpoch ?? "Current Era",
                Year = loreYear ?? 1492,
                Month = loreMonth ?? 1,
                Day = loreDay ?? 1,
                StartingHour = loreHour ?? 6
            };

            // GetOrCreateCampaignMetaAsync adopts a phantom meta doc (auto-vivified by an earlier
            // read tool against this slug) in place — sets System/DisplayName/IsSystemLocked/config
            // — instead of leaving the chosen system silently discarded (see the AlreadyExists guard
            // above for the case where a real, already-locked campaign already exists).
            var campaign = await GetOrCreateCampaignMetaAsync(session, normalized, initialSystem, displayName, forceLock: true);

            if (narrativeFocus is { Count: > 0 })
            {
                campaign.NarrativeFocus = narrativeFocus;
            }

            campaign.LoreSettings = loreSettings;

            // A phantom CampaignTime doc may already exist too (GetTimeAsync auto-vivifies with
            // default lore on any read against this slug) — reseed it to the lore actually chosen
            // here rather than silently keeping the earlier default.
            var existingTime = await session.LoadAsync<CampaignTime>(_keys.StateTime(normalized));
            if (existingTime != null)
            {
                existingTime.Epoch = loreSettings.Epoch;
                existingTime.Year = loreSettings.Year;
                existingTime.Month = loreSettings.Month;
                existingTime.Day = loreSettings.Day;
                existingTime.Hour = loreSettings.StartingHour;
                existingTime.TotalDaysElapsed = 0;
            }

            return new ToolResult<Campaign>(true, campaign,
                $"Campaign '{normalized}' created and locked to {initialSystem}. Pass campaignName='{normalized}' on subsequent calls.");
        });
    }

    internal Task<ToolResult<List<string>>> SetNarrativeFocus(
        List<string> tags,
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
Useful for discovering existing worlds. Pass the slug as campaignName on subsequent calls. Read-only, no side effects
— call it once to discover/confirm a slug, not repeatedly. A campaignName already known does not need re-discovery.")]
    public Task<ToolResult<List<CampaignSummaryView>>> ListCampaigns()
    {
        return ExecuteAsync(async session =>
        {
            var campaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/") && c.Id.EndsWith("/meta"))
                .Select(c => new CampaignSummaryView
                {
                    Name = c.Name,
                    DisplayName = c.DisplayName,
                    System = c.System,
                    CreatedAt = c.CreatedAt
                })
                .ToListAsync();

            return new ToolResult<List<CampaignSummaryView>>(true, campaigns, $"Found {campaigns.Count} campaign(s).");
        }, saveChanges: false);
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Reference lookup by kind. Rules: handbook, spells (className, level), creatures (query, levelMin/levelMax), items (query, category, tag), item_tags, level_up (characterId). Templates only; place live instances with world_build. " +
        "Engine: commit_schema (type='<one $type>' = its fields; none = index), help (topic: onboarding, world-building, commit-enum, tools, take-turn-modes, sessions, faq). Responses already carry guidance: don't call speculatively.")]
    public async Task<ToolResult<object>> Lookup(
        [Description("See tool description.")] string kind,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("commit_schema: one $type.")] string? type = null,
        [Description("help: topic.")] string? topic = null,
        [Description("spells: class, e.g. 'Wizard'.")] string? className = null,
        [Description("spells: level (0 = cantrip).")] int? level = null,
        [Description("Name substring (creatures, items).")] string? query = null,
        [Description("items: item category; commit_schema: Combat|Narrative|World|PlotThread.")] string? category = null,
        [Description("items: tag.")] string? tag = null,
        [Description("creatures: min level.")] int? levelMin = null,
        [Description("creatures: max level.")] int? levelMax = null,
        [Description("level_up: character id.")] string? characterId = null,
        [Description("Page offset.")] int offset = 0,
        [Description("Page size (default 40, max 100).")] int? limit = null)
    {
        var normalizedKind = kind?.Trim().ToLowerInvariant();
        switch (normalizedKind)
        {
            case "commit_schema":
                return Box(await new MetaTools().GetCommitSchema(category, type));
            case "help":
                return Box(await new MetaTools().GetHelp(topic));
        }

        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return await ToolArgumentErrors.Missing<object>(
                "campaignName",
                "Pass campaignName (the campaign slug).",
                toolName: "lookup");
        }

        return await GetRulesReference(campaignName, normalizedKind ?? "", className, level,
            nameQuery: query, levelMin: levelMin, levelMax: levelMax, offset: offset, limit: limit,
            characterId: characterId, itemNameQuery: query, itemCategory: category, itemTag: tag);
    }

    // Served through lookup(kind: <rules kind>); no longer an MCP tool of its own.
    [Description(
        "Ruleset reference lookup by 'kind': handbook (classes/races/feats/conditions), spells (className required; filter by level), creatures (stat-block templates), items (item templates), item_tags, level_up (characterId required; then commit one level_up change). Templates only: place live instances with world_build.")]
    public async Task<ToolResult<object>> GetRulesReference(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("What to look up: 'handbook', 'spells', 'creatures', or 'level_up'.")]
        string kind,
        [Description("spells only (required there): class name to list spells for, e.g. 'Wizard', 'Cleric'.")]
        string? className = null,
        [Description("spells only: spell level filter (0 = cantrip). Strongly recommended — full class lists are large.")]
        int? level = null,
        [Description("creatures only: creature name substring filter.")]
        string? nameQuery = null,
        [Description("creatures only: minimum level filter.")]
        int? levelMin = null,
        [Description("creatures only: maximum level filter.")]
        int? levelMax = null,
        [Description("Pagination offset (default 0).")]
        int offset = 0,
        [Description("spells/creatures: page size (default 40, max 100).")]
        int? limit = null,
        [Description("level_up only (required there): character ID, e.g. 'chars/hero-123'.")]
        string? characterId = null,
        [Description("items only: item name substring filter.")]
        string? itemNameQuery = null,
        [Description("items only: category (Weapon, Armor, Clothing, Container, Consumable, Tool, Material, Valuable, Document, Key, Other).")]
        string? itemCategory = null,
        [Description("items only: tag filter (e.g. 'exotic', 'kara-tur').")]
        string? itemTag = null)
    {
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "handbook":
                return Box(await GetSystemHandbook(campaignName));
            case "spells":
                if (string.IsNullOrWhiteSpace(className))
                {
                    return await ToolArgumentErrors.Missing<object>(
                        "className",
                        "kind:'spells' requires className (e.g. 'Wizard'). Get valid class names from kind:'handbook'.",
                        toolName: "lookup");
                }
                return Box(await GetSpells(className, campaignName, level, offset, limit));
            case "creatures":
                return Box(await QueryCreatures(campaignName, nameQuery, levelMin, levelMax, offset, limit));
            case "level_up":
                if (string.IsNullOrWhiteSpace(characterId))
                {
                    return await ToolArgumentErrors.Missing<object>(
                        "characterId",
                        "kind:'level_up' requires characterId (e.g. 'chars/hero-123').",
                        toolName: "lookup");
                }
                return Box(await GetPendingLevelUpChoices(characterId, campaignName));
            case "items":
                return Box(await GetItemDefinitions(campaignName, itemNameQuery, itemCategory?.Trim(), itemTag, offset, limit));
            case "item_tags":
                return Box(await GetItemTags(campaignName));
            default:
                return new ToolResult<object>(false, Error: ToolErrors.InvalidArgument,
                    Summary: $"Unknown kind '{kind}'. Use 'handbook', 'spells', 'creatures', 'items', 'item_tags', 'level_up', 'commit_schema', or 'help'.");
        }
    }

    private static ToolResult<object> Box<T>(ToolResult<T> r) =>
        new(r.Success, r.Data, r.Summary, r.Error, r.WorldPressure, r.RetryExample);

    internal Task<ToolResult<SystemHandbookResponse>> GetSystemHandbook(
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
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

    internal Task<ToolResult<SpellListResponse>> GetSpells(
        string @class,
        string campaignName,
        int? level = null,
        int offset = 0,
        int? limit = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
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

    internal Task<ToolResult<PendingLevelUpChoicesResponse>> GetPendingLevelUpChoices(
        string characterId,
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var character = await _repository.GetCharacterAsync(new CampaignSession(session, effective), characterId);
            if (character == null)
            {
                return new ToolResult<PendingLevelUpChoicesResponse>(
                    false,
                    Error: "CharacterNotFound",
                    Summary: $"Character {characterId} not found.");
            }

            var levelUpConfig = await session.LoadAsync<CampaignConfig>(_keys.Config(effective));
            var system = levelUpConfig?.ActiveSystem
                ?? throw new InvalidOperationException(
                    $"No campaign config found for '{effective}'; cannot determine its ruleset system.");

            var currentLevel = XpThresholdCalculator.GetCurrentLevel(character);
            var targetLevel = currentLevel + 1;
            var className = DetermineClassForLevelUp(character);

            var response = new PendingLevelUpChoicesResponse
            {
                CharacterId = characterId,
                ClassName = className,
                CurrentLevel = currentLevel,
                TargetLevel = targetLevel,
                System = system,
            };

            var levelDef = progressionProvider.GetLevelDefinition(system, className, targetLevel);
            if (levelDef == null)
            {
                response.Summary = $"No authored progression data for {className} at level {targetLevel} ({system}). "
                    + "Narrate the level-up choices yourself and commit a 'level_up' change with 'choices' describing them.";
                return new ToolResult<PendingLevelUpChoicesResponse>(true, response, response.Summary);
            }

            response.Features = levelDef.Features.Select(f =>
                string.IsNullOrWhiteSpace(f.Description) ? f.Name : $"{f.Name}: {f.Description}").ToList();

            response.Choices = levelDef.Choices.Select(c => new PendingLevelUpChoice
            {
                Key = c.Key,
                Prompt = c.Prompt,
                Type = c.Type,
                Required = c.Required,
                Options = c.Options,
                AbilityOptions = c.AbilityOptions,
            }).ToList();

            if (system == RulesetSystem.Pathfinder2e
                && (levelDef.ClassFeats is > 0 || levelDef.SkillFeats is > 0 || levelDef.GeneralFeats is > 0
                    || levelDef.AncestryFeats is > 0 || levelDef.AbilityBoosts is > 0))
            {
                response.Pf2eBudget = new Pf2eLevelBudget
                {
                    ClassFeats = levelDef.ClassFeats ?? 0,
                    SkillFeats = levelDef.SkillFeats ?? 0,
                    GeneralFeats = levelDef.GeneralFeats ?? 0,
                    AncestryFeats = levelDef.AncestryFeats ?? 0,
                    AbilityBoosts = levelDef.AbilityBoosts ?? 0,
                };
            }

            response.Summary = response.Choices.Count == 0 && response.Pf2eBudget == null
                ? $"{character.Name} reaches L{targetLevel} with no choices to make — just commit 'level_up'."
                : $"{character.Name} reaching L{targetLevel} ({className}): {response.Choices.Count} choice(s) to ask about"
                  + (response.Pf2eBudget != null ? " plus PF2e feat/ability budget." : ".");

            return new ToolResult<PendingLevelUpChoicesResponse>(true, response, response.Summary);
        }, saveChanges: false);
    }

    private static string DetermineClassForLevelUp(Character character)
    {
        if (string.IsNullOrWhiteSpace(character.ClassLevel))
            return "fighter";

        var firstPart = character.ClassLevel.Split('/')[0].Trim();
        var words = firstPart.Split(' ');
        return words.Length > 0 && words[0].Length > 0 ? words[0].ToLowerInvariant() : "fighter";
    }

    private static SpellSummaryView ToSpellSummary(SpellDefinition spell) =>
        new()
        {
            Name = spell.Name,
            Level = spell.Level ?? 0,
            Concentration = spell.Concentration ?? false,
            CastingTime = spell.CastingTime
        };

    internal Task<ToolResult<CreatureListResponse>> QueryCreatures(
        string campaignName,
        string? nameQuery = null,
        int? levelMin = null,
        int? levelMax = null,
        int offset = 0,
        int? limit = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
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

    internal Task<ToolResult<ItemDefinitionListResponse>> GetItemDefinitions(
        string campaignName,
        string? nameQuery = null,
        string? category = null,
        string? tag = null,
        int offset = 0,
        int? limit = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
            var system = config.ActiveSystem;
            var page = ItemDefinitionQueryBuilder.QueryPage(
                itemProvider, system, nameQuery, category, tag, offset, limit);

            var response = ItemDefinitionQueryBuilder.ToResponse(system, page);

            return new ToolResult<ItemDefinitionListResponse>(
                true,
                response,
                response.Hint);
        }, saveChanges: false);
    }

    internal Task<ToolResult<List<string>>> GetItemTags(
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
            var tags = itemProvider.GetDistinctTags(config.ActiveSystem);

            var hint = tags.Count == 0
                ? "No item tags found for this system yet — verify the system's items pack is loaded, or this is a fresh homebrew system."
                : $"{tags.Count} distinct tag(s) in use across item templates for '{config.ActiveSystem}'.";

            return new ToolResult<List<string>>(true, tags.ToList(), hint);
        }, saveChanges: false);
    }

    internal Task<ToolResult<CampaignContextView>> GetCurrentCampaign(
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (explicitName, session) =>
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