using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class WorldBuilderTools : CampaignToolBase
{
    private readonly CharacterBootstrapOrchestrator _bootstrap;

    public WorldBuilderTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap)
        : base(repository, keys)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

STRONGLY encouraged to populate:
- psychology.wants, psychology.fears, psychology.memories
- Detailed backstory in notes
- Schedule + Routines + StateModifiers
- needs.needDescriptors (human-readable explanations for any custom needs)
- Equipment via item_create in commit (set holderId to the character)

HP bootstrap: omit maxHp for PCs — engine derives from typed systemStats (hitDie, level, constitution, etc.).
Creature stat blocks: set maxHp OR systemStats.statBlockHp (not both needed). currentHp alone sets wounded state.
Put hitDie on dnd5e systemStats root (NOT in attributes). Class flavor goes in notes.

During play, prefer commit (character_create, level_up, activity) over repeated upserts.

Omitted fields are preserved: on an existing character, omitting psychology/social/needs/systemStats keeps the stored value; providing one replaces it wholesale.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The character to create or update. Strongly typed.")]
        CharacterUpsertRequest character,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var config = await s.LoadAsync<CampaignConfig>(_keys.Config(effective));
            var activeSystem = config?.ActiveSystem ?? RulesetSystem.Dnd5e;
            var merged = await _repository.UpsertCharacterAsync(s, character, effective);

            var hp = BootstrapHpResolver.Resolve(merged, null,
                character.CurrentHp > 0 ? character.CurrentHp : null);
            var report = await _bootstrap.ApplyCreationAsync(new BootstrapContext
            {
                Character = merged,
                ActiveSystem = activeSystem,
                ExplicitMaxHp = hp.ExplicitMaxHp,
                ExplicitCurrentHp = hp.ExplicitCurrentHp,
                Trigger = BootstrapTrigger.Upsert,
                Session = s,
                CampaignName = effective,
            });

            var extras = report.Messages
                .Concat(report.LlmHints.Select(h => $"[BOOTSTRAP HINT] {h}"))
                .ToList();
            var summary = extras.Count > 0
                ? $"Character upserted (campaign: {effective}). {string.Join(" ", extras)}"
                : $"Character upserted (campaign context: {effective}).";
            return new ToolResult<Character>(true, merged, summary);
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Create or overwrite a location on the world map.

Use for seeding new areas or replacing/updating full location documents — exits, parent links, ambientCrowd, pointsOfInterest, descriptions, and hierarchy.

Omitted fields are preserved: on an existing location, omitting exits/pointsOfInterest/pointOfInterestDetails/metadata keeps the stored value; providing one replaces it wholesale.

During play, prefer commit (location_create, location_update) for incremental changes; use upsert_location for bulk world-building or full replacements.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The location to create or update. Strongly typed.")]
        LocationUpsertRequest location,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertLocationAsync(s, location, effective);
            return new ToolResult<Location>(true, merged, $"Location upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists. Omitted fields are preserved: on existing lore, omitting tags/keywords keeps the stored value; providing one replaces it wholesale.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The lore entry to create or update. Strongly typed.")]
        LoreUpsertRequest lore,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertLoreAsync(s, lore, effective);
            return new ToolResult<Lore>(true, merged, $"Lore upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update an item (weapon, key, document, etc.). Use for seeding or bulk world-building. Omitted fields are preserved: on an existing item, omitting tags/distinctiveFeatures/properties keeps the stored value; providing one replaces it wholesale. During play, prefer commit (item_create) for incremental changes.")]
    public Task<ToolResult<Item>> UpsertItem(
        [Description("The item to create or update. Strongly typed.")]
        ItemUpsertRequest item,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertItemAsync(s, item, effective);
            return new ToolResult<Item>(true, merged, $"Item upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a plot thread — DM-scaffolding for a story arc's clues, tension, and resolution condition (usually not player-visible). Use for bulk-seeding clues or bumping tensionLevel without re-sending every clue. Omitted fields are preserved: on an existing thread, omitting clues/involvedEntityIds/foreshadowingHooks keeps the stored value; providing one replaces it wholesale.")]
    public Task<ToolResult<PlotThread>> UpsertPlotThread(
        [Description("The plot thread to create or update. Strongly typed.")]
        PlotThreadUpsertRequest plotThread,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertPlotThreadAsync(s, plotThread, effective);
            return new ToolResult<PlotThread>(true, merged, $"PlotThread upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Define or update a descriptor for a need type for a campaign slug. Automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list defined ones for the campaign. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(
        [Description("The name of the need (e.g., 'homesickness').")] string needName,
        [Description("The description of the need and its effects.")] string descriptor,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
        {
            return Task.FromResult(new ToolResult<string>(false, Error: "BadRequest",
                Summary: "needName and descriptor are required."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            await _repository.SetNeedDescriptorAsync(session, needName, descriptor, effective);
            return new ToolResult<string>(true, $"Descriptor for '{needName}' stored for campaign '{effective}'.",
                $"Descriptor persisted for campaign '{effective}'.");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Lists all defined need descriptors for the given campaign slug.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var descriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            return new ToolResult<Dictionary<string, string>>(true, descriptors,
                descriptors.Count > 0
                    ? $"Retrieved {descriptors.Count} need descriptors for campaign '{effective}'."
                    : $"No need descriptors defined yet for campaign '{effective}'.");
        }, saveChanges: false);
    }
}