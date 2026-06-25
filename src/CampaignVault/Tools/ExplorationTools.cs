using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class ExplorationTools : CampaignToolBase
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
        ICurrentCampaignContext currentCampaign,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator)
        : base(repository, currentCampaign, keys)
    {
        _behaviorSynthesizer = behaviorSynthesizer;
        _rulesetSelector = rulesetSelector;
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("KICKOFF TOOL: Call at session start for time, active rumors, recent history, and party location. Uses the campaign selected for this MCP session (select_campaign) unless campaignName is passed. partyLocationId is optional — omit if unknown and derive from recent history.")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is (string type). Optional. If not provided, you should determine the party's location from recent history or start them at a default location, then call 'get_scene' to load the location's details.")] string? partyLocationId = null,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        // We now save changes on reads because FilterAndCapAsync needs to persist PressureCooldowns.
        // The underlying repository methods are safe (e.g., GetSceneAsync only marks visited if explicitly requested).
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var time = await _repository.GetTimeAsync(session, effective);
            
            // Widen rumor search for kickoff
            var spreading = await _repository.QueryRumorsAsync(session, null, null, RumorState.Spreading, 3, effective);
            var peak = await _repository.QueryRumorsAsync(session, null, null, RumorState.Peak, 3, effective);
            var rumors = peak.Concat(spreading).ToList();

            var events = await _repository.QueryEventsAsync(session, null, null, 5, effective);
            
            Location? location = null;
            if (!string.IsNullOrEmpty(partyLocationId))
            {
                location = await _repository.GetLocationAsync(session, partyLocationId, effective);
            }
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var worldActiveQuests = await _repository.GetActiveQuestsAsync(session, effective, 10);

            var pressureCtx = new PressureContext(
                effective,
                time,
                config,
                session,
                ActiveRumors: rumors,
                RecentEvents: events.ToList(),
                QuestDeadlines: worldActiveQuests.Select(q => new QuestDeadlineInfo(q.Id, q.Title, q.DeadlineDay)).ToList());

            var finalPressures = await _pressureOrchestrator.CollectAndCapAsync(PressureScope.World, pressureCtx);

            var stuck = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective)
                            && c.CurrentActivity != null
                            && (c.CurrentActivity.StartsWith("Travel interrupted en route") || c.CurrentActivity.StartsWith("interrupted en route")))
                .Take(5)
                .ToListAsync();
            
            var suggestedExamples = new List<string>();
            var questPressureTriggered = finalPressures.Any(p => p.Contains("Quest", StringComparison.OrdinalIgnoreCase) || p.Contains("deadline", StringComparison.OrdinalIgnoreCase));
            if (questPressureTriggered && worldActiveQuests.Any())
            {
                var q = worldActiveQuests.First();
                suggestedExamples.Add($"[ {{ \"$type\": \"quest_progress\", \"questId\": \"{q.Id}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"We completed the objective.\" }} ]");
            }
            var stuckChar = stuck.FirstOrDefault();
            if (stuckChar != null && finalPressures.Any(p => p.Contains("Travel", StringComparison.OrdinalIgnoreCase) || p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
            {
                suggestedExamples.Add($"[ {{ \"$type\": \"activity\", \"characterId\": \"{stuckChar.Id}\", \"newActivity\": \"Resolved the ambush and continued\", \"updateLocation\": false }}, {{ \"$type\": \"travel\", \"characterId\": \"{stuckChar.Id}\", \"destinationLocationId\": \"locations/actual-dest\", \"encounterRiskModifier\": -30 }} ]");
            }

            var worldActiveFactions = await _repository.GetActiveFactionsAsync(session, effective, 10);
            
            var locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var travelEvent = events.FirstOrDefault(e => e.Category == EventCategory.Travel || (e.Category == EventCategory.Simulation && (e.Summary.Contains("Travel interrupted", StringComparison.OrdinalIgnoreCase) || e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase))));

            var view = new WorldStateView(
                time, 
                rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), 
                events, 
                locSummary, 
                finalPressures,
                worldActiveQuests.Select(CampaignRepository.ToActiveQuestSummary),
                worldActiveFactions.Select(f => 
                {
                    var overallStance = FactionStance.Neutral;
                    if (f.StanceToward != null && f.StanceToward.Count > 0)
                    {
                        if (f.StanceToward.Values.Contains(FactionStance.AtWar))
                        {
                            overallStance = FactionStance.AtWar;
                        }
                        else if (f.StanceToward.Values.Contains(FactionStance.Hostile))
                        {
                            overallStance = FactionStance.Hostile;
                        }
                        else if (f.StanceToward.Values.Contains(FactionStance.Allied))
                        {
                            overallStance = FactionStance.Allied;
                        }
                    }
                    return new FactionPresenceSummary(f.Id, f.Name, f.InfluenceLevel, overallStance, null, f.TerritoryLocationIds.Count);
                }),
                travelEvent?.Summary,
                suggestedExamples
            );
            var summary = $"Authoritative world state retrieved for session start (campaign: {effective}).";
            if (string.IsNullOrEmpty(partyLocationId))
            {
                summary += " HINT: partyLocationId was not provided. Review the recent history/events to identify where the party is, then call 'get_scene' with that location ID to load the scene's details, NPCs, and items.";
            }
            else if (location == null)
            {
                summary += $" WARNING: partyLocationId '{partyLocationId}' was not found in the database. The location may have been deleted or the ID may be incorrect. Verify the correct location ID from recent history and call 'get_scene' with a valid location ID.";
            }
            return new ToolResult<WorldStateView>(true, view, summary);
        }, saveChanges: true);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("EXPLORATION TOOL: Call when entering a room, building, or region. Returns location, NPCs, items, rumors, ActiveCombat, pressures. Uses session-selected campaign unless campaignName is passed.\nSet partyPresent=true ONLY when the party is physically entering or spending time here.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId,
        [Description("Set to true if the party is physically entering or spending time here (prevents cleanup).")] bool partyPresent = false,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var scene = await _repository.GetSceneAsync(session, locationId, effective, markVisited: partyPresent);
            var time = await _repository.GetTimeAsync(session, effective);
            var config = await _repository.GetCampaignConfigAsync(session, effective);

            var pressureCtx = new PressureContext(
                effective,
                time,
                config,
                session,
                QuestDeadlines: scene.ActiveQuests?.Select(q => new QuestDeadlineInfo(q.QuestId, q.Title, q.DeadlineDay)).ToList(),
                Scene: scene,
                RequestedLocationId: locationId,
                PartyPresent: partyPresent);

            var finalPressures = await _pressureOrchestrator.CollectAndCapAsync(PressureScope.Scene, pressureCtx);

            var suggestedExamples = new List<string>();
            var questPressureTriggered = finalPressures.Any(p => p.Contains("Quest", StringComparison.OrdinalIgnoreCase) || p.Contains("deadline", StringComparison.OrdinalIgnoreCase));
            if (questPressureTriggered && scene.ActiveQuests != null && scene.ActiveQuests.Any())
            {
                var q = scene.ActiveQuests.First();
                suggestedExamples.Add($"[ {{ \"$type\": \"quest_progress\", \"questId\": \"{q.QuestId}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"We completed the objective.\" }} ]");
            }
            var stuckChar = scene.PresentNPCs?.FirstOrDefault(c => c.CurrentActivity != null && c.CurrentActivity.Contains("interrupted en route", StringComparison.OrdinalIgnoreCase));
            if (stuckChar != null && finalPressures.Any(p => p.Contains("Travel", StringComparison.OrdinalIgnoreCase) || p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
            {
                suggestedExamples.Add($"[ {{ \"$type\": \"activity\", \"characterId\": \"{stuckChar.Id}\", \"newActivity\": \"Resolved the ambush and continued\", \"updateLocation\": false }}, {{ \"$type\": \"travel\", \"characterId\": \"{stuckChar.Id}\", \"destinationLocationId\": \"locations/actual-dest\", \"encounterRiskModifier\": -30 }} ]");
            }

            scene.SuggestedCommitExamples = suggestedExamples;

            return new ToolResult<SceneView>(true, scene, 
                $"Scene details for {locationId} (campaign: {effective}) retrieved.",
                WorldPressure: finalPressures.Length > 0 ? finalPressures : null);
        }, saveChanges: true);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("ROLEPLAY TOOL: Deep dive into an NPC's psychology — relationships, goals, fears, knowledge, mood, initiative signals. Uses session-selected campaign unless campaignName is passed.")]
    public Task<ToolResult<NpcContextView>> GetNpcContext(
        [Description("The unique ID of the character.")] string characterId,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return ToolArgumentErrors.Missing<NpcContextView>(
                "characterId",
                "Use get_scene or search_world to find the exact character ID.",
                toolName: "get_npc_context");
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
            if (npc == null)
            {
                return new ToolResult<NpcContextView>(false, Error: "NotFound");
            }

            // Use repo query (now scoped) + client filter for involved.
            var npcEvents = (await _repository.QueryEventsAsync(session, null, null, 10, effective))
                .Where(e => e.Involved != null && e.Involved.Contains(characterId))
                .OrderByDescending(e => e.Timestamp)
                .Take(10)
                .ToList();

            foreach (var ev in npcEvents)
            {
                _repository.SanitizeEvent(ev);   // reuses the central sanitization logic
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
                surfacedViaTool: "get_npc_context",
                includeTensionBreakdown: true,
                recentEvents: npcEvents);

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
            if (urgentInitiatives.Count > 0)
            {
                initiativePressure = await _pressureManager.FilterAndCapAsync(
                    session,
                    effective,
                    (int)time.TotalDaysElapsed,
                    urgentInitiatives);
            }

            var context = new NpcContextView
            {
                Character = npc,
                Psychology = npc.Psychology ?? new PsychologyProfile(),
                Social = npc.Social ?? new SocialProfile(),
                Needs = npc.Needs ?? new NeedsProfile(),
                SystemStats = npc.SystemStats ?? new SystemExtension(),
                RecentInteractions = npcEvents,
                BehavioralSummary = behavioralSummary,
                KnownNeeds = knownNeeds,
                NeedDescriptors = mergedDescriptors,
                BehavioralTension = enrichment.BehavioralTension,
                TensionComponents = enrichment.TensionComponents,
                ActiveInitiatives = enrichment.ActiveInitiatives.ToList(),
                RelevantMemories = enrichment.RelevantMemories.ToList()
            };

            return new ToolResult<NpcContextView>(
                true,
                context,
                $"Psychological context for {npc.Name} retrieved (campaign: {effective}).",
                WorldPressure: initiativePressure is { Length: > 0 } ? initiativePressure : null);
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("PARTY TOOL: Returns the active party roster — characters with isPc or isPartyCompanion for this campaign slug. Shared canon NPCs (e.g. Bob) are excluded. Uses session-selected campaign unless campaignName is passed.")]
    public Task<ToolResult<List<Character>>> GetParty(
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            var pcCount = party.Count(c => c.IsPc);
            var companionCount = party.Count - pcCount;
            return new ToolResult<List<Character>>(true, party,
                $"Retrieved {party.Count} party member(s) ({pcCount} PC(s), {companionCount} companion(s)) for campaign '{effective}'.");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("UNIFIED SEARCH: Keyword search across lore, characters, and locations (campaign-scoped plus shared-universe entities with no CampaignName). Uses session-selected campaign unless campaignName is passed.")]
    public Task<ToolResult<UnifiedSearchResult>> SearchWorld(
        [Description("The keyword or phrase to search for.")] string query,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var results = await _repository.UnifiedSearchAsync(session, query, effective);
            return new ToolResult<UnifiedSearchResult>(true, new UnifiedSearchResult(results), $"Found {results.Count()} matches (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("HISTORY RECALL: Keyword search over past events for the active campaign slug. Use to remember prior sessions or plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(
        [Description("The keyword or phrase to search for in historical events.")] string query, 
        [Description("Maximum number of events to return.")] int limit = 5,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var results = await _repository.QueryEventsAsync(session, query, null, limit, effective);
            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Returns an NPC's needs, values, and merged descriptors (campaign + per-NPC). Uses session-selected campaign unless campaignName is passed.")]
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(
        [Description("The unique ID of the character.")] string characterId,
        [Description(ToolParameterDescriptions.CampaignNameOptional)] string? campaignName = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
            if (npc == null)
            {
                return new ToolResult<NpcNeedsView>(false, Error: "NotFound");
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
}

public record UnifiedSearchResult(System.Collections.Generic.IEnumerable<object> Matches);
