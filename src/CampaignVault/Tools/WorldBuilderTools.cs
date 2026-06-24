using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
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
        ICurrentCampaignContext currentCampaign,
        CharacterBootstrapOrchestrator bootstrap)
        : base(repository, currentCampaign, keys)
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

During play, prefer commit (character_create, level_up, activity) over repeated upserts.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The full Character object to create or replace. Strongly typed.")]
        Character character,
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
        string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var config = await s.LoadAsync<CampaignConfig>(_keys.Config(effective));
            var activeSystem = config?.ActiveSystem ?? RulesetSystem.Dnd5e;
            var hp = BootstrapHpResolver.Resolve(character, null,
                character.CurrentHp > 0 ? character.CurrentHp : null);
            var report = await _bootstrap.ApplyCreationAsync(new BootstrapContext
            {
                Character = character,
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
            await _repository.UpsertCharacterAsync(s, character, effective);
            var summary = extras.Count > 0
                ? $"Character upserted (campaign: {effective}). {string.Join(" ", extras)}"
                : $"Character upserted (campaign context: {effective}).";
            return new ToolResult<Character>(true, character, summary);
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Create or overwrite a location on the world map.

Use for seeding new areas or replacing/updating full location documents — exits, parent links, ambientCrowd, pointsOfInterest, descriptions, and hierarchy. Repeated calls overwrite the stored location (upsert semantics).

During play, prefer commit (location_create, location_update) for incremental changes; use upsert_location for bulk world-building or full replacements.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The full Location object to create or replace. Strongly typed.")]
        Location location,
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
        string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            await _repository.UpsertLocationAsync(s, location, effective);
            return new ToolResult<Location>(true, location, $"Location upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")]
        Lore lore,
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
        string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            await _repository.UpsertLoreAsync(s, lore, effective);
            return new ToolResult<Lore>(true, lore, $"Lore upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool]
    [Description(
        "WORLD BUILDER TOOL: Define or update a descriptor for a need type for the current/selected campaign. Automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list defined ones for the campaign. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(
        string needName,
        string descriptor,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
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
    [Description("DISCOVERABILITY TOOL: Lists all defined need descriptors for the current (or specified) campaign.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        [Description(ToolParameterDescriptions.CampaignNameOptional)]
        string? campaignName = null)
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