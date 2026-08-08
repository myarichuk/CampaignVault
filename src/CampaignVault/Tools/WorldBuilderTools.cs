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
    [Description(@"WORLD BUILDER TOOL: Batch-create/update entities of any kind in one atomic call. This is the primary tool for initial world seeding (""session 0"") — seed locations, factions, characters, items, quests, and more in a single round-trip instead of one call per entity.

Each field is an optional, fully-typed array — e.g. `characters` takes the same fields as a character record (name, psychology, needs, systemStats, ...), `locations` takes the same fields as a location record (exits, pointsOfInterest, climateZone, ...). Include only the kinds you're seeding.

Dispatched in a fixed dependency order — locations, factions, creatures/spells/feats, characters, items, quests, plotThreads, lore, rumors, then needDescriptors — all within ONE session/save. A hard validation failure on any entry rolls back the ENTIRE batch and reports which entry (kind + index) failed; resend the full batch after fixing it, same as commit. Forward references to an entity later in the batch (or not yet created) are allowed and only produce a non-blocking warning.

Character entries get the full bootstrap treatment (HP/defense derivation). Capped at 100 total entries across all arrays — split larger seeds into multiple calls.

SYSTEMSTATS REQUIREMENT (Ruleset-dependent): Combat-capable NPCs MUST have systemStats matching the campaign's active ruleset (dnd5e, pf2e, narrative, etc.). For Dnd5e: include hitDie, level, abilities (Strength, Dexterity, etc.), and optional Attributes (passivePerception auto-derived, but add custom ones like morale, corruption, reputation). For Pf2e: similar structure. For Narrative: minimal statblock OK. See get_help topic=world-building for full examples per ruleset. Characters without systemStats cannot participate in combat, skill checks, or attribute tracking.

CHARACTERS DO NOT CARRY EQUIPMENT INLINE. A `characters[]` entry has no weapon/armor/gear fields — equipment is always a SEPARATE `items[]` entry in the SAME batch, with `holderId` set to the character's id (and `equipZones`/`equipLayer`/`isEquipped` if it should start worn). Seeding an armed guard, a soldier, a crime boss, or any combat-capable NPC without a matching `items[]` entry leaves them unarmed and unarmored — add the weapon/armor entries in the same call. A non-blocking warning is emitted for any newly-seeded character with no items[] entry (in this batch or already on file) so this is easy to miss but not silent.

See get_help topic=world-building for a full copy-paste example and recommended seeding order.")]
    public Task<ToolResult<WorldBuildResult>> WorldBuild(
        [Description("Batch of entities to create/update, grouped by kind. Each array is optional — include only the kinds you're seeding in this call.")]
        WorldBuildBatch batch,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var totalEntries = (batch.Locations?.Count ?? 0) + (batch.Factions?.Count ?? 0) + (batch.Creatures?.Count ?? 0)
                + (batch.Spells?.Count ?? 0) + (batch.Feats?.Count ?? 0) + (batch.Characters?.Count ?? 0)
                + (batch.Items?.Count ?? 0) + (batch.Quests?.Count ?? 0) + (batch.PlotThreads?.Count ?? 0)
                + (batch.WorldEvents?.Count ?? 0) + (batch.Lore?.Count ?? 0) + (batch.Rumors?.Count ?? 0) + (batch.NeedDescriptors?.Count ?? 0);

            if (totalEntries == 0)
            {
                throw new ArgumentException("world_build requires at least one entry across its arrays (locations, factions, creatures, spells, feats, characters, items, quests, plotThreads, worldEvents, lore, rumors, needDescriptors).");
            }

            if (totalEntries > 100)
            {
                throw new ArgumentException($"world_build batch has {totalEntries} entries, exceeding the 100-entry cap. Split into multiple calls.");
            }

            var result = new WorldBuildResult();
            var warnings = result.Warnings;

            await ProcessKindAsync(batch.Locations, "locations", CanonicalId.Locations, r => r.Id, ApplyLocationUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Factions, "factions", CanonicalId.Factions, r => r.Id, ApplyFactionUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Creatures, "creatures", CanonicalId.Creatures, r => r.Id, ApplyCreatureUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Spells, "spells", CanonicalId.Spells, r => r.Id, ApplySpellUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Feats, "feats", CanonicalId.Feats, r => r.Id, ApplyFeatUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Characters, "characters", CanonicalId.Characters, r => r.Id, ApplyCharacterUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Items, "items", CanonicalId.Items, r => r.Id, ApplyItemUpsertAsync, s, effective, result, warnings);
            await WarnOnUnequippedCharactersAsync(batch, s, effective, warnings);
            await ProcessKindAsync(batch.Quests, "quests", CanonicalId.Quests, r => r.Id, ApplyQuestUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.PlotThreads, "plotThreads", CanonicalId.PlotThreads, r => r.Id, ApplyPlotThreadUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.WorldEvents, "worldEvents", CanonicalId.WorldEvents, r => r.Id, ApplyWorldEventUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Lore, "lore", CanonicalId.Lore, r => r.Id, ApplyLoreUpsertAsync, s, effective, result, warnings);
            await ProcessKindAsync(batch.Rumors, "rumors", CanonicalId.Rumors, r => r.Id, ApplyRumorUpsertAsync, s, effective, result, warnings);

            if (batch.NeedDescriptors != null)
            {
                foreach (var (needName, descriptor) in batch.NeedDescriptors)
                {
                    if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
                    {
                        throw new ArgumentException($"needDescriptors['{needName}']: both needName and descriptor are required.");
                    }

                    await _repository.SetNeedDescriptorAsync(s, needName, descriptor, effective);
                    result.NeedDescriptorsSet++;
                }
            }

            var counts = result.Kinds.Count > 0
                ? string.Join(", ", result.Kinds.Select(kv => $"{kv.Key}: {kv.Value.Created} created/{kv.Value.Updated} updated"))
                : "no entities";
            var needText = result.NeedDescriptorsSet > 0 ? $", needDescriptors: {result.NeedDescriptorsSet} set" : "";
            var warningsText = warnings.Count > 0 ? $" Warnings: {string.Join(" | ", warnings)}" : "";
            var summary = $"world_build completed (campaign: {effective}). {counts}{needText}.{warningsText}";

            return new ToolResult<WorldBuildResult>(true, result, summary);
        });
    }

    /// <summary>
    /// Dispatches every element of one entity kind's array within a world_build batch: checks
    /// existence (for the created/updated count), calls the shared per-kind apply helper, records
    /// an ID-normalization warning when CanonicalId rewrote the supplied ID, and surfaces any
    /// WARNING-prefixed summary text non-blockingly. A validation failure (ArgumentException) is
    /// re-thrown with a "{kind}[{index}]" prefix and propagates out to abort/roll back the whole
    /// batch, mirroring commit's "resend full batch" model.
    /// </summary>
    private static async Task ProcessKindAsync<TReq, TEntity>(
        List<TReq>? items,
        string kind,
        string canonicalPrefix,
        Func<TReq, string> getId,
        Func<IAsyncDocumentSession, TReq, string, Task<ToolResult<TEntity>>> apply,
        IAsyncDocumentSession session,
        string effective,
        WorldBuildResult result,
        List<string> warnings)
    {
        if (items == null)
        {
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var req = items[i];
            var rawId = getId(req);
            var existedBefore = !string.IsNullOrWhiteSpace(rawId)
                && await session.Advanced.ExistsAsync(CanonicalId.Normalize(rawId, canonicalPrefix));

            ToolResult<TEntity> res;
            try
            {
                res = await apply(session, req, effective);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"{kind}[{i}] (id='{rawId}'): {ex.Message}", ex);
            }

            var canonicalId = getId(req); // Mutated in place by the repository call if it needed normalizing.
            if (canonicalId != rawId)
            {
                warnings.Add($"{kind}[{i}]: id '{rawId}' was normalized to '{canonicalId}'.");
            }

            if (!result.Kinds.TryGetValue(kind, out var kindResult))
            {
                kindResult = new WorldBuildKindResult();
                result.Kinds[kind] = kindResult;
            }

            if (existedBefore)
            {
                kindResult.Updated++;
            }
            else
            {
                kindResult.Created++;
                kindResult.CreatedIds.Add(canonicalId);
            }

            if (!string.IsNullOrEmpty(res.Summary) && res.Summary.Contains("WARNING", StringComparison.Ordinal))
            {
                warnings.Add($"{kind}[{i}]: {res.Summary}");
            }
        }
    }

    /// <summary>
    /// Non-blocking nudge: after characters + items dispatch in a world_build batch, flags any
    /// newly-seeded character with no item[] holding them — neither in this batch's items[] nor
    /// already on file from a prior call. Equipment is a separate entity (items[].holderId), not
    /// an inline character field, so this is easy for an LLM seeding a cast to forget silently.
    /// </summary>
    private static async Task WarnOnUnequippedCharactersAsync(
        WorldBuildBatch batch, IAsyncDocumentSession s, string effective, List<string> warnings)
    {
        if (batch.Characters is not { Count: > 0 })
        {
            return;
        }

        foreach (var character in batch.Characters)
        {
            if (string.IsNullOrWhiteSpace(character.Id))
                continue;

            if (character.SystemStats == null)
            {
                warnings.Add($"characters: '{character.Id}' has no systemStats — no HP/defense/abilities. " +
                    "For combat NPCs, provide systemStats with the appropriate ruleset (dnd5e, pf2e, etc.).");
            }
            else if ((character.SystemStats.Attributes?.Count ?? 0) == 0)
            {
                warnings.Add($"characters: '{character.Id}' has no custom attributes (willpower, morale, temperature, etc.). " +
                    "Consider adding Attributes to make this character mechanically/narratively richer.");
            }
        }

        var itemHolderIds = new HashSet<string>(
            (batch.Items ?? []).Select(i => i.HolderId).Where(h => !string.IsNullOrEmpty(h)),
            StringComparer.OrdinalIgnoreCase);

        var candidateIds = batch.Characters
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrEmpty(id) && !itemHolderIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidateIds.Count == 0)
        {
            return;
        }

        var existingHolders = new HashSet<string>(
            await s.Query<Item>()
                .Where(i => (i.CampaignName == effective || i.CampaignName == null) && i.HolderId.In(candidateIds))
                .Select(i => i.HolderId)
                .ToListAsync(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var charId in candidateIds)
        {
            if (!existingHolders.Contains(charId))
            {
                warnings.Add($"characters: '{charId}' has no items[] entry (holderId) — unarmed/unequipped. " +
                    $"If this NPC should carry a weapon/armor/gear, add an items[] entry with holderId=\"{charId}\".");
            }
        }
    }

    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

SYSTEMSTATS (REQUIRED FOR COMBAT): Match the campaign's active ruleset:
- Dnd5e: Provide hitDie, level, and core abilities (Strength, Dexterity, Wisdom, Intelligence, Charisma, Constitution). Engine auto-derives passivePerception and proficiencyBonus. Add Attributes for custom narrative mechanics (morale, willpower, corruption, reputation, etc.) — open-ended key/value dictionary (0-100 float range).
- Pf2e: Similar structure; provide level and key ability modifiers.
- Narrative/Generic: Minimal statblock OK (no combat resolution needed).
Characters without systemStats cannot use skill checks, combat, or attribute mechanics.

STRONGLY encouraged to populate:
- psychology.wants, psychology.fears, psychology.memories
- Detailed backstory in notes
- Schedule + Routines + StateModifiers
- needs.needDescriptors (human-readable explanations for any custom needs)
- Equipment via items (separate tool; set holderId to this character's id)
- Custom Attributes if NPC has interesting mechanical/narrative properties

HP bootstrap: omit maxHp for PCs — engine derives from typed systemStats (hitDie, level, constitution, etc.).
Creature stat blocks: set maxHp OR systemStats.statBlockHp (not both needed). currentHp alone sets wounded state.
Put hitDie on dnd5e systemStats root (NOT in attributes). Class flavor goes in notes.

This is the only tool that creates a new character. During play, use commit (level_up, activity, character_update, etc.) for changes to an existing one — do not re-call this to move or tweak a character you already created.

Omitted fields are preserved: on an existing character, omitting psychology/social/needs/systemStats keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<Character>> UpsertCharacter(
        [Description("The character to create or update. Strongly typed.")]
        CharacterUpsertRequest character,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var result = await ApplyCharacterUpsertAsync(s, character, effective);
            if (result.Success && result.Data?.Id is { } charId)
            {
                var hints = new List<string>();

                if (result.Data.SystemStats == null)
                {
                    hints.Add("HINT: No systemStats statblock provided — no HP/defense/abilities. " +
                        "For combat NPCs, provide systemStats with the appropriate ruleset (dnd5e, pf2e, etc.).");
                }
                else if ((result.Data.SystemStats.Attributes?.Count ?? 0) == 0)
                {
                    hints.Add("HINT: systemStats has no custom attributes (willpower, morale, temperature, corruption, etc.). " +
                        "Consider adding Attributes to make this character mechanically/narratively richer.");
                }

                if (!await s.Query<Item>().Where(i => (i.CampaignName == effective || i.CampaignName == null) && i.HolderId == charId).AnyAsync())
                {
                    hints.Add($"HINT: '{charId}' has no items on file (nothing with holderId=\"{charId}\") — unarmed/unequipped. " +
                        "Use world_build's items[] with holderId set to this character's id to give them a weapon/armor/gear.");
                }

                if (hints.Count > 0)
                {
                    var hintsText = string.Join(" | ", hints);
                    return new ToolResult<Character>(result.Success, result.Data, $"{result.Summary} {hintsText}", result.Error, result.WorldPressure, result.RetryExample);
                }
            }
            return result;
        });
    }

    private async Task<ToolResult<Character>> ApplyCharacterUpsertAsync(IAsyncDocumentSession s, CharacterUpsertRequest character, string effective)
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
        var merged = await _repository.UpsertCharacterAsync(new CampaignSession(s, effective), character);

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
                "If this campaign should use a different ruleset, call create_campaign with the correct initialSystem BEFORE creating more characters, then re-upsert this one with corrected systemStats.");
        }

        if (existedBefore)
        {
            extras.Add(
                $"WARNING: Character '{character.Id}' already existed and was merged/overwritten by this call, not newly created. " +
                "Omitted fields on this request were preserved, but every field you DID supply replaced the prior value. " +
                "If you intended a small in-play change (HP, activity, location), prefer commit instead of re-calling world_build.");
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
    }

    [Description(@"WORLD BUILDER TOOL: Create or overwrite a location on the world map.

Use for seeding new areas or replacing/updating full location documents — exits, parent links, ambientCrowd, pointsOfInterest, descriptions, and hierarchy.

Omitted fields are preserved: on an existing location, omitting exits/pointsOfInterest/pointOfInterestDetails/metadata keeps the stored value; providing one replaces it wholesale.

This is the only tool that creates a new location. During play, use commit's location_update for incremental changes to an existing one.")]
    internal Task<ToolResult<Location>> UpsertLocation(
        [Description("The location to create or update. Strongly typed.")]
        LocationUpsertRequest location,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyLocationUpsertAsync(s, location, effective));
    }

    private async Task<ToolResult<Location>> ApplyLocationUpsertAsync(IAsyncDocumentSession s, LocationUpsertRequest location, string effective)
    {
        var merged = await _repository.UpsertLocationAsync(new CampaignSession(s, effective), location);
        var warning = await WarnDanglingReferencesAsync(s,
            ("controllingFactionId", location.ControllingFactionId));
        var summary = $"Location upserted (campaign context: {effective}).{warning}";
        return new ToolResult<Location>(true, merged, summary);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists. Omitted fields are preserved: on existing lore, omitting tags/keywords keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<Lore>> UpsertLore(
        [Description("The lore entry to create or update. Strongly typed.")]
        LoreUpsertRequest lore,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyLoreUpsertAsync(s, lore, effective));
    }

    private async Task<ToolResult<Lore>> ApplyLoreUpsertAsync(IAsyncDocumentSession s, LoreUpsertRequest lore, string effective)
    {
        var merged = await _repository.UpsertLoreAsync(new CampaignSession(s, effective), lore);
        return new ToolResult<Lore>(true, merged, $"Lore upserted (campaign context: {effective}).");
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update an item (weapon, key, document, etc.). This is the only tool that creates a new item. Pass itemDetails to seed persistent, granular state at creation — scratches, stains, secret compartments, existing damage or wear — instead of issuing separate item_update commits afterward. Omitted fields are preserved: on an existing item, omitting tags/distinctiveFeatures/properties keeps the stored value; providing one replaces it wholesale [itemDetails is the exception — it is creation-only and is ignored (not replaced/merged) when the item already exists]. During play, use commit's item_update (tags/state) or item_update's upsertItemDetail (persistent damage/wear/hidden features) for incremental changes to an existing item.")]
    internal Task<ToolResult<Item>> UpsertItem(
        [Description("The item to create or update. Strongly typed.")]
        ItemUpsertRequest item,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyItemUpsertAsync(s, item, effective));
    }

    private async Task<ToolResult<Item>> ApplyItemUpsertAsync(IAsyncDocumentSession s, ItemUpsertRequest item, string effective)
    {
        var alreadyExisted = await s.LoadAsync<Item>(CanonicalId.Normalize(item.Id, CanonicalId.Items)) != null;
        var merged = await _repository.UpsertItemAsync(new CampaignSession(s, effective), item);
        var message = $"Item upserted (campaign context: {effective}).";
        var nudges = ItemUpsertSanityChecker.GetNudges(item, alreadyExisted);
        if (nudges.Count > 0)
        {
            message += " " + string.Join(" ", nudges);
        }
        return new ToolResult<Item>(true, merged, message);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a homebrew creature stat-block template. These are reusable reference templates (distinct from live NPC/monster instances, which use world_build's characters[]). Homebrew creatures override SRD creatures by name when queried via get_rules_reference (kind:'creatures'). Omitted fields are preserved: on an existing creature, omitting skills/abilities keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<CustomCreature>> UpsertCreature(
        [Description("The creature to create or update. Strongly typed.")]
        CustomCreatureUpsertRequest creature,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyCreatureUpsertAsync(s, creature, effective));
    }

    private async Task<ToolResult<CustomCreature>> ApplyCreatureUpsertAsync(IAsyncDocumentSession s, CustomCreatureUpsertRequest creature, string effective)
    {
        var merged = await _repository.UpsertCustomCreatureAsync(s, creature, effective);
        return new ToolResult<CustomCreature>(true, merged, $"Creature upserted (campaign context: {effective}).");
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a plot thread — DM-scaffolding for a story arc's clues, tension, and resolution condition (usually not player-visible). Use for bulk-seeding clues or bumping tensionLevel without re-sending every clue. Omitted fields are preserved: on an existing thread, omitting clues/involvedEntityIds/foreshadowingHooks keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<PlotThread>> UpsertPlotThread(
        [Description("The plot thread to create or update. Strongly typed.")]
        PlotThreadUpsertRequest plotThread,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyPlotThreadUpsertAsync(s, plotThread, effective));
    }

    private async Task<ToolResult<PlotThread>> ApplyPlotThreadUpsertAsync(IAsyncDocumentSession s, PlotThreadUpsertRequest plotThread, string effective)
    {
        var merged = await _repository.UpsertPlotThreadAsync(new CampaignSession(s, effective), plotThread);
        var refs = (plotThread.InvolvedEntityIds ?? [])
            .Select((id, i) => ($"involvedEntityIds[{i}]", (string?)id));
        var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
        var summary = $"PlotThread upserted (campaign context: {effective}).{warning}";
        return new ToolResult<PlotThread>(true, merged, summary);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a world event (scheduled, time-based, or conditional). Scripted consequences that auto-fire based on triggers, with optional prevention conditions. Omitted fields are preserved: on an existing event, omitting involvedEntityIds/effects keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<WorldEvent>> UpsertWorldEvent(
        [Description("The world event to create or update. Strongly typed.")]
        WorldEventUpsertRequest worldEvent,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyWorldEventUpsertAsync(s, worldEvent, effective));
    }

    private async Task<ToolResult<WorldEvent>> ApplyWorldEventUpsertAsync(IAsyncDocumentSession s, WorldEventUpsertRequest worldEvent, string effective)
    {
        var merged = await _repository.UpsertWorldEventAsync(s, worldEvent, effective);
        var refs = (worldEvent.InvolvedEntityIds ?? [])
            .Select((id, i) => ($"involvedEntityIds[{i}]", (string?)id))
            .Concat([("actorId", worldEvent.ActorId)]);
        var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
        var summary = $"WorldEvent upserted (campaign context: {effective}).{warning}";
        return new ToolResult<WorldEvent>(true, merged, summary);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a homebrew spell. Overrides SRD spells by name when queried via get_rules_reference (kind:'spells'). Omitted fields are preserved: on an existing spell, omitting classes keeps the stored value; providing one replaces it wholesale.")]
    internal Task<ToolResult<CustomSpell>> UpsertSpell(
        [Description("The spell to create or update. Strongly typed.")]
        CustomSpellUpsertRequest spell,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplySpellUpsertAsync(s, spell, effective));
    }

    private async Task<ToolResult<CustomSpell>> ApplySpellUpsertAsync(IAsyncDocumentSession s, CustomSpellUpsertRequest spell, string effective)
    {
        var merged = await _repository.UpsertCustomSpellAsync(s, spell, effective);
        return new ToolResult<CustomSpell>(true, merged, $"Spell upserted (campaign context: {effective}).");
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a homebrew feat/perk. Overrides SRD feats by name when queried via get_rules_reference (kind:'handbook').")]
    internal Task<ToolResult<CustomFeat>> UpsertFeat(
        [Description("The feat to create or update. Strongly typed.")]
        CustomFeatUpsertRequest feat,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyFeatUpsertAsync(s, feat, effective));
    }

    private async Task<ToolResult<CustomFeat>> ApplyFeatUpsertAsync(IAsyncDocumentSession s, CustomFeatUpsertRequest feat, string effective)
    {
        var merged = await _repository.UpsertCustomFeatAsync(s, feat, effective);
        return new ToolResult<CustomFeat>(true, merged, $"Feat upserted (campaign context: {effective}).");
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a faction. Omitted fields are preserved: on an existing faction, omitting territoryLocationIds/knownLeaderIds keeps the stored value; providing one replaces it wholesale. For reputation/stance changes to an existing faction, prefer commit (faction_reputation, faction_state).")]
    internal Task<ToolResult<Faction>> UpsertFaction(
        [Description("The faction to create or update. Strongly typed.")]
        FactionUpsertRequest faction,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyFactionUpsertAsync(s, faction, effective));
    }

    private async Task<ToolResult<Faction>> ApplyFactionUpsertAsync(IAsyncDocumentSession s, FactionUpsertRequest faction, string effective)
    {
        var wasNew = await s.LoadAsync<Models.Faction>(faction.Id) is null;
        var merged = await _repository.UpsertFactionAsync(new CampaignSession(s, effective), faction);
        var refs = (faction.TerritoryLocationIds ?? [])
            .Select((id, i) => ($"territoryLocationIds[{i}]", (string?)id))
            .Concat((faction.KnownLeaderIds ?? []).Select((id, i) => ($"knownLeaderIds[{i}]", (string?)id)));
        var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
        var seedHint = wasNew ? EntitySeedingAdvisor.GenerateWorldEventSeedingHint(merged, effective) : null;
        var summary = $"Faction upserted (campaign context: {effective}).{warning}{seedHint}";
        return new ToolResult<Faction>(true, merged, summary);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a quest. Omitted fields are preserved: on an existing quest, omitting objectives/relatedLocationIds/relatedFactionIds keeps the stored value; providing one replaces it wholesale. For objective-state or narrative progress on an existing quest, prefer commit (quest_progress).")]
    internal Task<ToolResult<Quest>> UpsertQuest(
        [Description("The quest to create or update. Strongly typed.")]
        QuestUpsertRequest quest,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyQuestUpsertAsync(s, quest, effective));
    }

    private async Task<ToolResult<Quest>> ApplyQuestUpsertAsync(IAsyncDocumentSession s, QuestUpsertRequest quest, string effective)
    {
        var merged = await _repository.UpsertQuestAsync(new CampaignSession(s, effective), quest);
        var refs = new List<(string, string?)> { ("giverId", quest.GiverId) }
            .Concat((quest.RelatedLocationIds ?? []).Select((id, i) => ($"relatedLocationIds[{i}]", (string?)id)))
            .Concat((quest.RelatedFactionIds ?? []).Select((id, i) => ($"relatedFactionIds[{i}]", (string?)id)));
        var warning = await WarnDanglingReferencesAsync(s, refs.ToArray());
        var summary = $"Quest upserted (campaign context: {effective}).{warning}";
        return new ToolResult<Quest>(true, merged, summary);
    }

    [Description(
        "WORLD BUILDER TOOL: Create or update a rumor. regionLocationId is required when creating a new rumor. For rumor evolution over time on an existing rumor, prefer commit (rumor).")]
    internal Task<ToolResult<Rumor>> UpsertRumor(
        [Description("The rumor to create or update. Strongly typed.")]
        RumorUpsertRequest rumor,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, (effective, s) => ApplyRumorUpsertAsync(s, rumor, effective));
    }

    private async Task<ToolResult<Rumor>> ApplyRumorUpsertAsync(IAsyncDocumentSession s, RumorUpsertRequest rumor, string effective)
    {
        var merged = await _repository.UpsertRumorAsync(s, rumor, effective);
        return new ToolResult<Rumor>(true, merged, $"Rumor upserted (campaign context: {effective}).");
    }

    internal Task<ToolResult<string>> DefineNeedDescriptor(
        string needName,
        string descriptor,
        string campaignName)
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

    internal Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var descriptors = await _repository.GetGlobalNeedDescriptorsAsync(new CampaignSession(session, effective));
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
