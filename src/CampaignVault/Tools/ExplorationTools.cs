using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using static CampaignVault.Data.ClimateCycle;
using static CampaignVault.Data.ClimateResolver;

namespace CampaignVault.Tools;

[McpServerToolType]
public class ExplorationTools : CampaignToolBase, IMcpServerTool
{

    public const string UrgentGroupingKey = "NpcInitiative:Urgent";

    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;
    private readonly IRulesetModuleSelector _rulesetSelector;

    public ExplorationTools(
        CampaignRepository repository,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        IRulesetModuleSelector rulesetSelector,
        CampaignDocumentKeys keys,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator,
        ILogger<ExplorationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _behaviorSynthesizer = behaviorSynthesizer;
        _rulesetSelector = rulesetSelector;
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
    }

    internal Task<ToolResult<WorldStateView>> GetWorldState(
        string campaignName,
        string? partyLocationId = null)
    {
        // We now save changes on reads because FilterAndCapAsync needs to persist PressureCooldowns.
        // The underlying repository methods are safe (e.g., GetSceneAsync only marks visited if explicitly requested).
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var view = await _repository.BuildWorldStateAsync(session, effective, partyLocationId, _pressureOrchestrator);

            var summary = $"Authoritative world state retrieved for session start (campaign: {effective}).";
            if (string.IsNullOrEmpty(partyLocationId))
            {
                summary += " HINT: partyLocationId was not provided. Review the recent history/events to identify where the party is, then call get_entity with that location ID to load the scene's details, NPCs, and items.";
            }
            else if (view.PartyLocation == null)
            {
                summary += $" WARNING: partyLocationId '{partyLocationId}' was not found in the database. The location may have been deleted or the ID may be incorrect. Verify the correct location ID from recent history and call get_entity with a valid location ID.";
            }
            return new ToolResult<WorldStateView>(true, view, summary);
        }, saveChanges: true);
    }

    internal Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("Set to true if the party is physically entering or spending time here (prevents cleanup).")] bool partyPresent = false)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var scene = await _repository.GetSceneAsync(session, locationId, effective, markVisited: partyPresent);
            var time = await _repository.GetTimeAsync(session, effective);
            var config = await _repository.GetCampaignConfigAsync(session, effective);

            var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, scene.Location);
            var ambientTemp = ClimateCycle.GetTemperatureCelsius(zone, time.Hour);
            scene.Climate = new SceneClimateSummary(
                zone.ToString(),
                ambientTemp,
                time.GetTimeOfDayName()
            );

            var pressureCtx = new PressureContext(
                effective,
                time,
                config,
                session,
                QuestDeadlines: scene.ActiveQuests?.Select(q => new QuestDeadlineInfo(q.QuestId, q.Title, q.DeadlineDay)).ToList(),
                Scene: scene,
                RequestedLocationId: locationId,
                PartyPresent: partyPresent);

            var pressureItems = await _pressureOrchestrator.CollectAndCapAsync(PressureScope.Scene, pressureCtx);
            var finalPressures = PressureManager.ToDisplayStrings(pressureItems);

            // Query associated plot threads
            var associatedThreads = await _repository.GetPlotThreadsReferencingEntityAsync(session, locationId, effective);
            scene.AssociatedPlotThreads = associatedThreads
                .Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel))
                .ToList();

            var stuckChar = scene.PresentNPCs?.FirstOrDefault(c => c.CurrentActivity != null && c.CurrentActivity.Contains("interrupted en route", StringComparison.OrdinalIgnoreCase));
            scene.SuggestedCommitExamples = SuggestedCommitExampleBuilder.Build(
                pressureItems,
                scene.ActiveQuests?.FirstOrDefault()?.QuestId,
                stuckChar?.Id);
            scene.WorldPressureItems = pressureItems;

            var summary = $"Scene details for {locationId} (campaign: {effective}) retrieved.";
            if (partyPresent && scene.Location != null && scene.PresentNPCs != null)
            {
                int npcCount = scene.PresentNPCs.Count();
                string? hint = LocationPlausibilityAdvisor.GenerateSuggestion(scene.Location, npcCount);
                if (!string.IsNullOrEmpty(hint))
                {
                    summary += hint;
                }
            }

            return new ToolResult<SceneView>(true, scene, summary,
                WorldPressure: finalPressures.Length > 0 ? finalPressures : null);
        }, saveChanges: true);
    }

    internal Task<ToolResult<NpcContextView>> GetNpcContext(
        [Description("The unique ID of the character.")] string characterId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return ToolArgumentErrors.Missing<NpcContextView>(
                "characterId",
                "Use search_world to find the exact character ID.",
                toolName: "get_entity");
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
            if (npc == null)
            {
                var suggestion = EntitySeedingAdvisor.GenerateSuggestion(characterId, effective);
                return new ToolResult<NpcContextView>(false, Error: "NotFound", Summary: suggestion);
            }

            var config = await _repository.GetCampaignConfigAsync(session, effective);

            // Filtered at the index level (involvedCharacterId) and importance-ranked so the budget
            // doesn't silently drop older Core/Important events that genuinely involve this NPC.
            var npcEvents = await _repository.SelectRecentEventsAsync(session, effective,
                config.EventContextBudgetNpc, involvedCharacterId: characterId);

            foreach (var ev in npcEvents)
            {
                JsonSanitizer.Sanitize(ev);
            }

            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, null, npcEvents);

            var knownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>();
            // Merge global + per-NPC descriptors (per-NPC wins) for full context
            var globalDescriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            var npcDescriptors = npc.Needs?.NeedDescriptors ?? new Dictionary<string, string>();
            var mergedDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcDescriptors)
            {
                mergedDescriptors[kv.Key] = kv.Value;
            }

            var enrichment = await _repository.EnrichNpcInitiativeAsync(
                session,
                npc,
                effective,
                surfacedViaTool: "get_entity",
                includeTensionBreakdown: true,
                recentEvents: npcEvents);

            var heldItems = await session.Query<Item>()
                .Where(i => i.HolderId == characterId && !i.IsArchived)
                .ToListAsync();
            var equipped = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList();
            var carried = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList();

            // Query associated plot threads
            var associatedThreads = await _repository.GetPlotThreadsReferencingEntityAsync(session, characterId, effective);
            var associatedMinimal = associatedThreads
                .Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel))
                .ToList();

            var time = await _repository.GetTimeAsync(session, effective);
            string[]? initiativePressure = null;
            var urgentInitiatives = enrichment.ActiveInitiatives
                .Where(i => i.Urgency >= MemoryUrgency.High)
                .Select(i => new WorldPressureItem(
                    PressureSeverity.NarrativePrompt,
                    characterId,
                    $"{npc.Name} — {i.FramingPrompt}",
                    UrgentGroupingKey))
                .ToList();
            List<WorldPressureItem> initiativeItems = [];
            if (urgentInitiatives.Count > 0)
            {
                initiativeItems = await _pressureManager.FilterAndCapAsync(
                    session,
                    effective,
                    (int)time.TotalDaysElapsed,
                    urgentInitiatives);
                initiativePressure = PressureManager.ToDisplayStrings(initiativeItems);
            }

            var context = new NpcContextView
            {
                Character = CharacterDetailView.From(npc),
                RecentInteractions = npcEvents.Select(EventSummaryView.From).ToList(),
                BehavioralSummary = behavioralSummary,
                KnownNeeds = knownNeeds,
                NeedDescriptors = mergedDescriptors,
                BehavioralTension = enrichment.BehavioralTension,
                TensionComponents = enrichment.TensionComponents,
                ActiveInitiatives = enrichment.ActiveInitiatives.ToList(),
                RelevantMemories = enrichment.RelevantMemories.ToList(),
                Equipped = equipped,
                Carried = carried,
                TurnIntent = enrichment.TurnIntent,
                AssociatedPlotThreads = associatedMinimal
            };

            var equipmentHint = heldItems.Count == 0
                ? $" HINT: '{characterId}' has no items on file (nothing with holderId=\"{characterId}\") — unarmed/unequipped. " +
                  "If this NPC should be carrying a weapon/armor/gear, add it via world_build's items[]."
                : "";

            return new ToolResult<NpcContextView>(
                true,
                context,
                $"Psychological context for {npc.Name} retrieved (campaign: {effective}).{equipmentHint}",
                WorldPressure: initiativePressure is { Length: > 0 } ? initiativePressure : null);
        }, saveChanges: true);
    }

    internal Task<ToolResult<List<PartyMemberView>>> GetParty(string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            foreach (var member in party)
            {
                await _repository.UpgradeSystemStatsIfNeededAsync(session, member, effective);
            }

            var partyMembers = new List<PartyMemberView>();
            foreach (var member in party)
            {
                var heldItems = await session.Query<Item>()
                    .Where(i => i.HolderId == member.Id && !i.IsArchived)
                    .ToListAsync();
                var equipped = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList();
                var carried = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList();
                partyMembers.Add(new PartyMemberView(CharacterDetailView.From(member), equipped, carried));
            }

            var pcCount = party.Count(c => c.IsPc);
            var companionCount = party.Count - pcCount;
            return new ToolResult<List<PartyMemberView>>(true, partyMembers,
                $"Retrieved {party.Count} party member(s) ({pcCount} PC(s), {companionCount} companion(s)) for campaign '{effective}'.");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("UNIFIED SEARCH: Hybrid keyword + semantic search across characters, lore, locations, rumors, factions, quests, events, and items (campaign-scoped plus shared-universe entities with no CampaignName). Each match is { entityType, match: <summary> } — entityType disambiguates types that share field names (e.g. character/location/faction/item all have 'name'). Summaries are lean, not full documents; use get_entity with the matched id for full detail (chars/, locations/, factions/, quests/, items/ — rumors and lore have no get_entity route, so their summaries already include full text). Requires campaignName.")]
    public Task<ToolResult<UnifiedSearchResult>> SearchWorld(
        [Description("The keyword or phrase to search for.")] string query,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var results = await _repository.UnifiedSearchAsync(session, query, effective);
            var count = results.Count();
            var summary = $"Found {count} matches (campaign: {effective}).";
            if (count == 0)
            {
                summary += "\n\n💡 **No matches found.** If the party is looking for a specific location, NPC, item, or quest that doesn't exist yet, consider seeding it with `world_build`. See `get_help topic=world-building` or the dnd-exploration skill for lazy-seeding guidelines.";
            }
            return new ToolResult<UnifiedSearchResult>(true, new UnifiedSearchResult(results), summary);
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("HISTORY RECALL (GROUND TRUTH): Hybrid keyword + semantic search over past events for the active campaign slug, optionally filtered by locationId and/or involvedCharacterId. Use this to check what ACTUALLY happened — e.g. 'was Bob a witness to the robbery' — as distinct from an NPC's full-detail view (get_entity with a chars/ id), which returns what the NPC subjectively believes/remembers (and may have drifted from the truth). Use to remember prior sessions or plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("The keyword or phrase to search for in historical events. Optional if filtering purely by locationId/involvedCharacterId.")] string query = "",
        [Description("Maximum number of events to return. Defaults to the campaign's recall event budget (CampaignConfig.EventContextBudgetRecall, 5 unless configured).")] int? limit = null,
        [Description("Optional. Only return events at this location ID (or with this ID among relatedLocationIds).")] string? locationId = null,
        [Description("Optional. Only return events where this character ID appears in 'involved' — i.e. ground-truth presence, not subjective memory.")] string? involvedCharacterId = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var effectiveLimit = limit ?? config.EventContextBudgetRecall;

            // Empty query: pure browse, ranked by importance then recency (same as ambient context).
            // Non-empty query: unchanged keyword-priority-then-vector relevance via QueryEventsAsync/Hybrid.
            IEnumerable<Event> results = string.IsNullOrWhiteSpace(query)
                ? await _repository.SelectRecentEventsAsync(session, effective, effectiveLimit, locationId, involvedCharacterId)
                : await _repository.QueryEventsAsync(session, query, null, effectiveLimit, effective, locationId, involvedCharacterId);

            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events (campaign: {effective}).");
        }, saveChanges: false);
    }

    internal Task<ToolResult<NpcNeedsView>> GetNpcNeeds(
        string characterId,
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
            if (npc == null)
            {
                var suggestion = EntitySeedingAdvisor.GenerateSuggestion(characterId, effective);
                return new ToolResult<NpcNeedsView>(false, Error: "NotFound", Summary: suggestion);
            }

            // Merge global descriptors (from DefineNeedDescriptor) with per-NPC ones.
            // Per-NPC descriptors take precedence on conflicts.
            var globalDescriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            var npcDescriptors = npc.Needs?.NeedDescriptors ?? new Dictionary<string, string>();
            var mergedDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcDescriptors)
            {
                mergedDescriptors[kv.Key] = kv.Value;
            }

            var view = new NpcNeedsView
            {
                CharacterId = npc.Id,
                Name = npc.Name,
                KnownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>(),
                NeedDescriptors = mergedDescriptors
            };

            return new ToolResult<NpcNeedsView>(true, view, $"Needs for {npc.Name} retrieved (campaign: {effective}).");
        }, saveChanges: false);
    }

    internal Task<ToolResult<SceneSummaryView>> GetSceneSummary(
        [Description("The unique ID of the location.")] string locationId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var summary = await _repository.BuildSceneSummaryAsync(session, locationId, effective);
            if (summary == null)
            {
                var suggestion = EntitySeedingAdvisor.GenerateSuggestion(locationId, effective);
                return new ToolResult<SceneSummaryView>(false, Error: "NotFound", Summary: suggestion);
            }

            return new ToolResult<SceneSummaryView>(true, summary,
                $"Scene summary for {locationId} (campaign: {effective}) retrieved.");
        }, saveChanges: false);
    }

    internal Task<ToolResult<NpcSummaryView>> GetNpcSummary(
        [Description("The unique ID of the character.")] string characterId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var summary = await _repository.BuildNpcSummaryAsync(session, characterId, effective);
            if (summary == null)
            {
                var suggestion = EntitySeedingAdvisor.GenerateSuggestion(characterId, effective);
                return new ToolResult<NpcSummaryView>(false, Error: "NotFound", Summary: suggestion);
            }

            return new ToolResult<NpcSummaryView>(true, summary,
                $"NPC summary for {summary.Name} retrieved (campaign: {effective}).");
        }, saveChanges: false);
    }
}

public record UnifiedSearchResult(System.Collections.Generic.IEnumerable<object> Matches);
