using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class WorldBuilderTools : CampaignToolBase, IMcpServerTool
{
    private readonly CharacterBootstrapOrchestrator _bootstrap;

    public WorldBuilderTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ILogger<WorldBuilderTools>? logger = null)
        : base(repository, keys, logger)
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
- Equipment via upsert_item (set holderId to the character)

HP bootstrap: omit maxHp for PCs — engine derives from typed systemStats (hitDie, level, constitution, etc.).
Creature stat blocks: set maxHp OR systemStats.statBlockHp (not both needed). currentHp alone sets wounded state.
Put hitDie on dnd5e systemStats root (NOT in attributes). Class flavor goes in notes.

This is the only tool that creates a new character. During play, use commit (level_up, activity, character_update, etc.) for changes to an existing one — do not re-call this to move or tweak a character you already created.

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
            var noConfigYet = config is null;
            if (noConfigYet)
            {
                // No ruleset has been configured for this campaign yet (create_campaign/set_active_system
                // haven't run). Persist the Dnd5e assumption we're about to bootstrap against so it's a
                // durable, visible fact instead of a throwaway local default — see A1 in the tool-usage audit.
                config = new CampaignConfig { Id = _keys.Config(effective), ActiveSystem = RulesetSystem.Dnd5e };
                await s.StoreAsync(config, config.Id);
            }

            var activeSystem = config!.ActiveSystem;
            // Session-tracked load; cheap even though UpsertCharacterAsync loads the same ID again below.
            var existedBefore = !string.IsNullOrWhiteSpace(character.Id)
                && await s.LoadAsync<Character>(character.Id) is not null;
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
            if (noConfigYet)
            {
                extras.Add(
                    $"NOTE: No campaign ruleset is configured yet for '{effective}' — this character was bootstrapped assuming {RulesetSystem.Dnd5e}. " +
                    "If this campaign should use a different ruleset, call create_campaign or set_active_system BEFORE creating more characters, then re-upsert this one with corrected systemStats.");
            }

            if (existedBefore)
            {
                extras.Add(
                    $"WARNING: Character '{character.Id}' already existed and was merged/overwritten by this call, not newly created. " +
                    "Omitted fields on this request were preserved, but every field you DID supply replaced the prior value. " +
                    "If you intended a small in-play change (HP, activity, location), prefer commit instead of re-calling upsert_character.");
            }

            if (!string.IsNullOrWhiteSpace(character.CurrentLocationId)
                && await s.LoadAsync<Location>(character.CurrentLocationId) is null)
            {
                extras.Add(
                    $"WARNING: currentLocationId '{character.CurrentLocationId}' does not currently exist. " +
                    "This is allowed (create the location before the party reaches it), but verify the ID is correct.");
            }

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

This is the only tool that creates a new location. During play, use commit's location_update for incremental changes to an existing one.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The location to create or update. Strongly typed.")]
        LocationUpsertRequest location,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertLocationAsync(s, location, effective);
            var warning = await WarnDanglingReferencesAsync(s,
                ("controllingFactionId", location.ControllingFactionId));
            var summary = $"Location upserted (campaign context: {effective}).{warning}";
            return new ToolResult<Location>(true, merged, summary);
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
        "WORLD BUILDER TOOL: Create or update an item (weapon, key, document, etc.). This is the only tool that creates a new item. Omitted fields are preserved: on an existing item, omitting tags/distinctiveFeatures/properties keeps the stored value; providing one replaces it wholesale. During play, use commit's item_update/item for incremental changes to an existing item.")]
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
        "WORLD BUILDER TOOL: Create or update a homebrew creature stat-block template. These are reusable reference templates (distinct from live NPC/monster instances, which use upsert_character). Homebrew creatures override SRD creatures by name when queried via query_creatures. Omitted fields are preserved: on an existing creature, omitting skills/abilities keeps the stored value; providing one replaces it wholesale.")]
    public Task<ToolResult<CustomCreature>> UpsertCreature(
        [Description("The creature to create or update. Strongly typed.")]
        CustomCreatureUpsertRequest creature,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertCustomCreatureAsync(s, creature, effective);
            return new ToolResult<CustomCreature>(true, merged, $"Creature upserted (campaign context: {effective}).");
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
            var refs = (plotThread.InvolvedEntityIds ?? [])
                .Select((id, i) => ($"involvedEntityIds[{i}]", (string?)id));
            var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
            var summary = $"PlotThread upserted (campaign context: {effective}).{warning}";
            return new ToolResult<PlotThread>(true, merged, summary);
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a homebrew spell. Overrides SRD spells by name when queried via get_spells. Omitted fields are preserved: on an existing spell, omitting classes keeps the stored value; providing one replaces it wholesale.")]
    public Task<ToolResult<CustomSpell>> UpsertSpell(
        [Description("The spell to create or update. Strongly typed.")]
        CustomSpellUpsertRequest spell,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertCustomSpellAsync(s, spell, effective);
            return new ToolResult<CustomSpell>(true, merged, $"Spell upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a homebrew feat/perk. Overrides SRD feats by name when queried via get_system_handbook.")]
    public Task<ToolResult<CustomFeat>> UpsertFeat(
        [Description("The feat to create or update. Strongly typed.")]
        CustomFeatUpsertRequest feat,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertCustomFeatAsync(s, feat, effective);
            return new ToolResult<CustomFeat>(true, merged, $"Feat upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a faction. Omitted fields are preserved: on an existing faction, omitting territoryLocationIds/knownLeaderIds keeps the stored value; providing one replaces it wholesale. For reputation/stance changes to an existing faction, prefer commit (faction_reputation, faction_state).")]
    public Task<ToolResult<Faction>> UpsertFaction(
        [Description("The faction to create or update. Strongly typed.")]
        FactionUpsertRequest faction,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertFactionAsync(s, faction, effective);
            var refs = (faction.TerritoryLocationIds ?? [])
                .Select((id, i) => ($"territoryLocationIds[{i}]", (string?)id))
                .Concat((faction.KnownLeaderIds ?? []).Select((id, i) => ($"knownLeaderIds[{i}]", (string?)id)));
            var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
            var summary = $"Faction upserted (campaign context: {effective}).{warning}";
            return new ToolResult<Faction>(true, merged, summary);
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a quest. Omitted fields are preserved: on an existing quest, omitting objectives/relatedLocationIds/relatedFactionIds keeps the stored value; providing one replaces it wholesale. For objective-state or narrative progress on an existing quest, prefer commit (quest_progress).")]
    public Task<ToolResult<Quest>> UpsertQuest(
        [Description("The quest to create or update. Strongly typed.")]
        QuestUpsertRequest quest,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertQuestAsync(s, quest, effective);
            var refs = new List<(string, string?)> { ("giverId", quest.GiverId) }
                .Concat((quest.RelatedLocationIds ?? []).Select((id, i) => ($"relatedLocationIds[{i}]", (string?)id)))
                .Concat((quest.RelatedFactionIds ?? []).Select((id, i) => ($"relatedFactionIds[{i}]", (string?)id)));
            var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
            var summary = $"Quest upserted (campaign context: {effective}).{warning}";
            return new ToolResult<Quest>(true, merged, summary);
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "WORLD BUILDER TOOL: Create or update a rumor. regionLocationId is required when creating a new rumor. For rumor evolution over time on an existing rumor, prefer commit (rumor).")]
    public Task<ToolResult<Rumor>> UpsertRumor(
        [Description("The rumor to create or update. Strongly typed.")]
        RumorUpsertRequest rumor,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var merged = await _repository.UpsertRumorAsync(s, rumor, effective);
            return new ToolResult<Rumor>(true, merged, $"Rumor upserted (campaign context: {effective}).");
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

    /// <summary>
    /// Checks a set of (fieldName, referencedId) pairs for existence and returns a single
    /// non-blocking warning string (empty if all referenced IDs exist or are unset) to append
    /// to a tool's Summary. Forward references (e.g. a quest giver not created yet) are allowed
    /// by design — this only makes the gap visible instead of silent. See C3/C4 in the tool-usage audit.
    /// </summary>
    private static async Task<string> WarnDanglingReferencesAsync(
        IAsyncDocumentSession session, params (string FieldName, string? Id)[] references)
    {
        var missing = new List<string>();
        foreach (var (fieldName, id) in references)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!await session.Advanced.ExistsAsync(id))
            {
                missing.Add($"{fieldName}='{id}'");
            }
        }

        return missing.Count == 0
            ? string.Empty
            : $" WARNING: the following referenced ID(s) do not currently exist: {string.Join(", ", missing)}. " +
              "This is allowed (create them before they're needed), but verify the ID(s) are correct.";
    }
}