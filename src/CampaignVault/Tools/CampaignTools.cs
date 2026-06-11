using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Raven.Client.Exceptions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using CampaignVault.Rulesets;
using System.Threading.RateLimiting;
// ReSharper disable UnusedMember.Global

namespace CampaignVault.Tools;

internal static class ToolErrors
{
    public const string NotFound = "NotFound";
    public const string StateDrift = "StateDriftConflict";
    public const string InternalError = "InternalError";
    public const string RateLimitExceeded = "RateLimitExceeded";
    public const string BadRequest = "BadRequest";
}

[McpServerToolType]
public class CampaignTools
{
    public const string EventGroupingKey = "Simulation:Event";
    public const string UrgentGroupingKey = "NpcInitiative:Urgent";

    private readonly CampaignRepository _repository;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly IRulesetModuleSelector _rulesetSelector;
    private readonly CampaignDocumentKeys _keys;
    private readonly ICurrentCampaignContext _currentCampaign;
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;

    private static readonly RateLimiter CommitRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 10000, // Large enough for parallel xUnit test suites, still guards against infinite loops
        TokensPerPeriod = 1000, 
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        AutoReplenishment = true
    });

    // Modern / DI constructor (all services provided)
    public CampaignTools(
        CampaignRepository repository,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        IRulesetModuleSelector rulesetSelector,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign,
        IPressureManager? pressureManager = null,
        IPressureOrchestrator? pressureOrchestrator = null)
    {
        _repository = repository;
        _behaviorSynthesizer = behaviorSynthesizer;
        _rulesetSelector = rulesetSelector;
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign ?? new CurrentCampaignContext();
        _pressureManager = pressureManager ?? new PressureManager(_keys);
        _pressureOrchestrator = pressureOrchestrator ?? new PressureOrchestrator(
            DefaultPressureContributors.All(),
            _pressureManager,
            rulesetSelector);
    }

    private string EffectiveCampaign(string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        return string.IsNullOrWhiteSpace(_currentCampaign.CurrentCampaignName) ? "default" : _currentCampaign.CurrentCampaignName;
    }

    /// <summary>
    /// Central helper for campaign lifecycle. Ensures both the Campaign meta document
    /// and its corresponding CampaignConfig exist. Used by CreateCampaign, SelectCampaign,
    /// and SetActiveSystem to keep meta creation logic in one place and enforce lock-in semantics.
    /// </summary>
    private async Task<Campaign> GetOrCreateCampaignMetaAsync(IAsyncDocumentSession session, string normalizedName, RulesetSystem defaultSystem, string? displayName = null, bool forceLock = false)
    {
        var campaignId = _keys.Meta(normalizedName);
        var campaign = await session.LoadAsync<Campaign>(campaignId);
        if (campaign == null)
        {
            campaign = new Campaign
            {
                Id = campaignId,
                Name = normalizedName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedName : displayName,
                System = defaultSystem,
                IsSystemLocked = forceLock
            };
            await session.StoreAsync(campaign, campaignId);

            var configId = _keys.Config(normalizedName);
            var config = await session.LoadAsync<CampaignConfig>(configId);
            if (config == null)
            {
                config = new CampaignConfig
                {
                    Id = configId,
                    ActiveSystem = defaultSystem
                };
                await session.StoreAsync(config, configId);
            }
        }
        return campaign;
    }

    private async Task<ToolResult<T>> ExecuteAsync<T>(Func<IAsyncDocumentSession, Task<ToolResult<T>>> action, bool saveChanges = true)
    {
        int maxRetries = 2;
        int attempt = 0;

        while (true)
        {
            using var session = _repository.OpenSession();

            ToolResult<T> result;
            try
            {
                result = await action(session);
            }
            catch (ConcurrencyException)
            {
                if (++attempt <= maxRetries) continue;
                return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "State changed mid-operation. Re-fetch and retry.");
            }
            catch (Exception ex)
            {
                return new ToolResult<T>(false, Error: ToolErrors.InternalError, Summary: ex.Message);
            }

            if (!result.Success)
            {
                return result;
            }

            if (saveChanges)
            {
                try
                {
                    await session.SaveChangesAsync();
                }
                catch (ConcurrencyException)
                {
                    if (++attempt <= maxRetries) continue;
                    return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "Commit failed due to concurrent modification. Re-fetch and retry.");
                }
            }

            // Final sanitizing step on every tool response.
            // This guarantees that even if a polluted entity reached this point (legacy data,
            // unsanitized query path, etc.), nothing containing a live or dead JsonElement
            // will be serialized by the MCP layer's System.Text.Json when sending the response.
            _repository.SanitizeForToolResponse(result.Data);

            return result;
        }
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the time, active rumors, recent history, and current party location in one view. Respects the currently selected campaign (via select_campaign). partyLocationId is optional — omit it if you do not know the party's current location and derive it from recent history instead.")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is (string type). Optional. If not provided, you should determine the party's location from recent history or start them at a default location, then call 'get_scene' to load the location's details.")] string? partyLocationId = null,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        // We now save changes on reads because FilterAndCapAsync needs to persist PressureCooldowns.
        // The underlying repository methods are safe (e.g., GetSceneAsync only marks visited if explicitly requested).
        return ExecuteAsync(async session => {
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
            
            var travelEvent = events.FirstOrDefault(e => e.Summary.Contains("travel", StringComparison.OrdinalIgnoreCase) || e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase) || e.Summary.Contains("interrupted", StringComparison.OrdinalIgnoreCase));

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
    [Description("EXPLORATION TOOL: Call this whenever entering a new room, building, or region. Returns the location description, present NPCs (with behavioral summaries), visible items, and local rumors. Respects the currently selected campaign.\nSet 'partyPresent=true' ONLY if the party is physically entering or spending time here. Leave false if just looking around for pressures to prevent messing up the simulation's character eviction logic.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId,
        [Description("Set to true if the party is physically entering or spending time here (prevents cleanup).")] bool partyPresent = false,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
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

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(@"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world.
Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove, ruleset_action, and the open-world creates/updates). 
Use ActivityChange liberally to keep get_scene in sync with your narrative. 

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided (the primary laziness mitigation).**

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, auto-linking, and many more copy-paste examples.

Supported types for $type: hp, item, item_update, status, statusremove, event, rumor, relationship, engagement_relation, spatial_position, need, attribute, mood, activity, ruleset_action, location_create, location_update, character_create, character_update, system_stats, knowledge_update, schedule_change, item_create, travel, rest, faction_create, faction_reputation, faction_state, quest_create, quest_progress.

=== RECOMMENDED PATTERNS (copy-paste friendly) ===

(See get_help for the full expanded list including the tavern creation + promotion flow, one-way link fixes, ambient/PoI flavor without bloat, etc.)

Basic + creating on the fly examples are also shown in the tool description and get_help.")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Array of world changes. Each item must be a JSON object with a '$type' discriminator.")] WorldChange[] changes,
        [Description("Narrative summary of what happened (for the log and world pressure).")] string narrative,
        [Description("Optional campaign name. Falls back to currently selected campaign.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);

        if (changes.Length == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.BadRequest, Summary: "Commit requires at least one change."));
        }

        if (changes.Length > 50)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: $"Commit rejected: Too many changes in a single batch ({changes.Length}). Maximum allowed is 50."));
        }

        if (!CommitRateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        return ExecuteAsync(async session => {
            var result = await _repository.StageChangesAsync(session, changes, effective);
            if (!result.Success)
            {
                var errorMsg = string.Join("\n", result.Summary);
                return new ToolResult<CommitResult>(false, result, Summary: "Commit failed due to validation errors.", Error: errorMsg);
            }
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), CampaignName = effective, Summary = narrative, Category = EventCategory.SceneCommit, Involved = result.InvolvedEntities }, effective);
            var msg = $"World updated with {changes.Length} changes.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
    }

    /// <summary>
    /// Fallback for callers (or future clients) that can only easily emit a raw JSON string for the changes batch.
    /// Parses to WorldChange[] and delegates to the primary MCP Commit implementation.
    /// Not exposed as an MCP tool.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));
        }

        WorldChange[] elements;
        try
        {
            var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowOutOfOrderMetadataProperties = true };
            elements = JsonSerializer.Deserialize<WorldChange[]>(changesJson, serializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: $"Invalid changes JSON: {ex.Message}"));
        }

        return Commit(elements, narrative, campaignName); // respects context + explicit override
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("TIME PASSAGE: Call this for travel, long rests, or downtime. Fast-forwards the world clock and runs background simulations (rumor decay, NPC needs). Returns narrative updates on what changed while the party was away. Respects the currently selected campaign.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Number of days to skip.")] int days, 
        [Description("The resulting time of day.")] TimeOfDay timeOfDay,
        [Description("Summary of the rest or travel activity.")] string narrative,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        if (days < 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest", Summary: "Cannot advance a negative number of days."));
        }

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var result = await _repository.AdvanceWorldAsync(session, days, timeOfDay, effective);
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.Timeskip });

            var timeDoc = await _repository.GetTimeAsync(session, effective);
            
            string[]? cappedPressure = null;
            var rawPressures = result.SimulatorEvents
                .Select(e => new WorldPressureItem(PressureSeverity.Simulation, "Simulation", e, EventGroupingKey))
                .Concat(result.WorldPressure)
                .ToList();

            if (rawPressures.Count > 0)
            {
                cappedPressure = await _pressureManager.FilterAndCapAsync(session, effective, (int)timeDoc.TotalDaysElapsed, rawPressures);
            }

            return new ToolResult<AdvanceResult>(true, result, 
                $"Advanced {days} days. {result.SimulatorEvents.Count} events and {result.WorldPressure.Count} structured pressures generated.",
                WorldPressure: cappedPressure != null && cappedPressure.Length > 0 ? cappedPressure : null);
        });
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("ROLEPLAY TOOL: Deep dive into an NPC's psychological state. Returns their relationships, goals, fears, knowledge, and current emotional mood. Respects the currently selected campaign for need descriptors etc.")]
    public Task<ToolResult<NpcContextView>> GetNpcContext(
        string characterId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
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
        });
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("PARTY TOOL: Retrieve all player characters (PCs) and major KeepAlive characters in the campaign. Returns their current HP, Max HP, location, activity, and key stats/attributes.")]
    public Task<ToolResult<List<Character>>> GetParty(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective) && c.KeepAlive)
                .ToListAsync();

            return new ToolResult<List<Character>>(true, party, $"Retrieved {party.Count} party/KeepAlive characters (campaign: {effective}).");
        });
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DEEP DIVE TOOL: Returns the full Faction document (stances, influence, territory, leaders, metadata, DM notes) for a known faction ID. Use this (instead of guessing from get_scene summaries) when you need to roleplay faction reactions, declare war, expand territory, or check player rep impact. Campaign-scoped.")]
    public Task<ToolResult<Faction>> GetFactionContext(
        [Description("Exact faction ID e.g. 'factions/thieves-guild' (use fuzzy search or get_scene first if unsure).")] string factionId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var faction = await _repository.GetFactionAsync(session, factionId, effective);
            if (faction == null)
            {
                var suggestions = await _repository.SuggestFactionsAsync(session, factionId, effective);
                var hint = suggestions.Any() 
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"))
                    : "";
                return new ToolResult<Faction>(false, Error: "NotFound", Summary: $"Faction '{factionId}' not found.{hint} Use exact ID from get_scene or search.");
            }
            return new ToolResult<Faction>(true, faction, $"Full faction context for {faction.Name} (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Deep dives")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DEEP DIVE TOOL: Returns the full Quest document (all objectives with states, deadlines, rewards, giver, related locations/factions, DM notes, urgency). Use when get_scene shows an ActiveQuestSummary and you need to advance/fail specific objectives or check stakes. Supports per-objective deadlines from Phase 7.3.")]
    public Task<ToolResult<Quest>> GetQuestDetails(
        [Description("Exact quest ID e.g. 'quests/rats_01'.")] string questId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var quest = await _repository.GetQuestAsync(session, questId, effective);
            if (quest == null)
            {
                var suggestions = await _repository.SuggestQuestsAsync(session, questId, effective);
                var hint = suggestions.Any() ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Title})")) : "";
                return new ToolResult<Quest>(false, Error: "NotFound", Summary: $"Quest '{questId}' not found.{hint}");
            }
            return new ToolResult<Quest>(true, quest, $"Quest details for '{quest.Title}' (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("UNIFIED SEARCH: Search across Lore, Characters, Locations, and Items in one shot. Use this when searching for anything by name or keyword. (Campaign context is recorded for future per-campaign scoping.)")]
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(
        string query,
        [Description("Optional campaign name. Falls back to currently selected (for future namespacing).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteAsync(async session => {
            var results = await _repository.UnifiedSearchAsync(session, query, effective);
            return new ToolResult<IEnumerable<object>>(true, results, $"Found {results.Count()} matches (campaign: {effective}).");
        }, saveChanges: false);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("HISTORY RECALL: Semantic search over past events. Use this to remember 'what happened last time we were here' or recall specific plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(
        string query, 
        int limit = 5,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var results = await _repository.QueryEventsAsync(session, query, null, limit, effective);
            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events (campaign: {effective}).");
        }, saveChanges: false);
    }

    // --- Configuration Tools (Genuine state setup) ---

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

STRONGLY encouraged to populate:
- Mind.Wants, Mind.Fears, Mind.Knows
- Detailed backstory in Notes
- Schedule + Routines + StateModifiers
- Mind.NeedDescriptors (human-readable explanations for any custom needs)
- Equipment via Items (set HolderId to the character)

This is the best opportunity to create deep, simulatable NPCs.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The full Character object to create or replace. Strongly typed.")] Character character,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertCharacterAsync(s, character, effective);
            return new ToolResult<Character>(true, character, $"Character upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Register a new location on the world map. For first-time setup only.

Define hierarchical locations with exits, parent relationships, and rich metadata.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The full Location object to create or replace. Strongly typed.")] Location location,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLocationAsync(s, location, effective);
            return new ToolResult<Location>(true, location, $"Location upserted (campaign context: {effective}).");
        });
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")] Lore lore,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLoreAsync(s, lore, effective);
            return new ToolResult<Lore>(true, lore, $"Lore upserted (campaign context: {effective}).");
        });
    }

    // --- Needs Discoverability Tools ---

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Returns all known needs for an NPC along with their current values and any descriptors. Use this to understand what psychological or physical drives an NPC has before roleplaying or making changes. The needs system is open — you are encouraged to invent new narrative-appropriate needs.")]
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(
        string characterId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
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

    [ToolCategory("World builder")]
    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Define or update a descriptor for a need type for the current/selected campaign. Automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list defined ones for the campaign. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
        {
            return Task.FromResult(new ToolResult<string>(false, Error: "BadRequest", Summary: "needName and descriptor are required."));
        }

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            await _repository.SetNeedDescriptorAsync(session, needName, descriptor, effective);
            return new ToolResult<string>(true, $"Descriptor for '{needName}' stored for campaign '{effective}'.", $"Descriptor persisted for campaign '{effective}'.");
        });
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Lists all defined need descriptors for the current (or specified) campaign.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var descriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            return new ToolResult<Dictionary<string, string>>(true, descriptors, 
                descriptors.Count > 0 
                    ? $"Retrieved {descriptors.Count} need descriptors for campaign '{effective}'."
                    : $"No need descriptors defined yet for campaign '{effective}'.");
        }, saveChanges: false);
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Get the current campaign configuration.
Returns the ruleset and system-specific options (e.g., house rules). Respects the currently selected campaign.")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description("Optional campaign name. Falls back to the currently selected campaign (via select_campaign).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            return new ToolResult<CampaignConfig>(true, config, $"Campaign configuration retrieved for '{effective}'.");
        }, saveChanges: false);
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Set the active ruleset system for a campaign.
Respects lock-in (cannot change system once locked). Use this to define house rules or system options.

Example: set_active_system(RulesetSystem.Pf2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")] RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")] Dictionary<string, string>? systemOptions = null,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);

        return ExecuteAsync(async session =>
        {
            var campaign = await GetOrCreateCampaignMetaAsync(session, effective, activeSystem, forceLock: false);

            if (campaign.IsSystemLocked && campaign.System != activeSystem)
            {
                return new ToolResult<CampaignConfig>(
                    false,
                    Error: "SystemLocked",
                    Summary: $"The ruleset for campaign '{effective}' is locked to {campaign.System}. Cannot change to {activeSystem}.");
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

            return new ToolResult<CampaignConfig>(true, config, $"Active ruleset for '{effective}' set to '{activeSystem}' (locked).");
        });
    }

    // --- Combat & Dispatch Tools ---

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Starts a new combat encounter at the specified location.
Rolls initiative for all combatants based on the active ruleset system and establishes the turn order. If a combat is already active, it is overwritten. Respects the currently selected campaign.

Example: start_combat(""locations/tavern"", [""chars/pc1"", ""chars/pc2"", ""monsters/goblin1""])")]
    public Task<ToolResult<CombatEncounter>> StartCombat(
        [Description("The location ID where combat is happening.")] string locationId,
        [Description("List of character IDs participating in combat.")] string[] combatantIds,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            if (combatantIds == null || combatantIds.Length == 0)
            {
                return new ToolResult<CombatEncounter>(false, Error: "InvalidInput", Summary: "Cannot start combat with zero combatants.");
            }

            var uniqueIds = combatantIds.Distinct().ToList();
            var loadedCharacters = await session.LoadAsync<Character>(uniqueIds);
            var validCharacters = loadedCharacters.Values.Where(c => c != null && c.CurrentHp > 0).ToList();

            if (validCharacters.Count == 0)
            {
                return new ToolResult<CombatEncounter>(false, Error: "InvalidInput", Summary: "None of the specified combatants are valid and alive.");
            }

            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var module = _rulesetSelector.GetModule(config.ActiveSystem);

            var combatants = new List<CombatantState>();
            foreach (var character in validCharacters)
            {
                var initiative = await module.Combat.RollInitiativeAsync(character);
                combatants.Add(new CombatantState
                {
                    CharacterId = character.Id,
                    Initiative = initiative,
                    HasActedThisRound = false
                });
            }

            // Sort by highest initiative first
            combatants = combatants.OrderByDescending(c => c.Initiative).ToList();

            var encounter = new CombatEncounter
            {
                Id = _keys.CombatCurrent(effective),
                LocationId = locationId,
                Round = 1,
                Combatants = combatants,
                ActiveTurnId = combatants.FirstOrDefault()?.CharacterId,
                IsActive = true
            };

            await session.StoreAsync(encounter, encounter.Id);

            return new ToolResult<CombatEncounter>(true, encounter, $"Combat started at {locationId} with {combatants.Count} combatants.");
        });
    }


    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Advances the turn order to the next combatant.
If all combatants have acted, advances to the next round. Skips dead combatants (HP <= 0).
Round-based status effects naturally expire during this transition when their round duration ends.
Respects the currently selected campaign.")]
    public Task<ToolResult<CombatEncounter>> NextTurn(
        [Description("Optional. If provided, the command will fail if the current active turn does not match this ID. Helps prevent accidental double-advancing.")] string? expectedActiveTurnId = null,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        var combatId = _keys.CombatCurrent(effective);

        return ExecuteAsync(async session =>
        {
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounter>(false, Error: "NotFound", Summary: "No active combat encounter.");
            }

            if (!string.IsNullOrWhiteSpace(expectedActiveTurnId) && encounter.ActiveTurnId != expectedActiveTurnId)
            {
                return new ToolResult<CombatEncounter>(false, Error: "StateDrift", Summary: $"Expected active turn to be '{expectedActiveTurnId}' but it was '{encounter.ActiveTurnId}'. The combat state has drifted.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Mark current actor as having acted
            var current = encounter.Combatants.FirstOrDefault(c => c.CharacterId == encounter.ActiveTurnId);
            if (current != null)
            {
                current.HasActedThisRound = true;
            }

            var expiredMessages = new List<string>();

            // Find next who hasn't acted and is alive
            CombatantState? GetNextAliveUnacted() => encounter.Combatants.FirstOrDefault(c => 
                !c.HasActedThisRound && 
                characters.TryGetValue(c.CharacterId, out var character) && character != null && character.CurrentHp > 0);

            var next = GetNextAliveUnacted();
            
            if (next == null)
            {
                // Verify if anyone is actually alive
                if (!encounter.Combatants.Any(c => characters.TryGetValue(c.CharacterId, out var character) && character != null && character.CurrentHp > 0))
                {
                     return new ToolResult<CombatEncounter>(false, Error: "CombatEnded", Summary: "No valid and alive combatants remain. Combat has ended or cannot proceed.");
                }

                // New round
                encounter.Round++;
                foreach (var c in encounter.Combatants) c.HasActedThisRound = false;
                next = GetNextAliveUnacted(); // Retrieve the first alive person again

                // Expire round-based status effects
                foreach (var character in characters.Values.Where(c => c != null))
                {
                    if (character.SystemStats?.StatusEffects != null)
                    {
                        var effects = character.SystemStats.StatusEffects;
                        var toRemove = effects.Where(e => e.ExpiresAtRound.HasValue && e.ExpiresAtRound.Value <= encounter.Round).ToList();
                        foreach (var effect in toRemove)
                        {
                            effects.Remove(effect);
                            expiredMessages.Add($"Expired effect '{effect.Name}' on '{character.Name}'.");
                        }
                    }
                }
            }

            encounter.ActiveTurnId = next?.CharacterId;
            await session.StoreAsync(encounter, encounter.Id);

            var summary = $"Advanced to turn of {encounter.ActiveTurnId} (Round {encounter.Round}).";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Ends the current active combat encounter and wraps up the state.
Aggressively clears all round-based status effects (e.g., 'until end of combat' effects) from all combatants.
Day-based effects remain active. Respects the currently selected campaign.")]
    public Task<ToolResult<CombatEncounter>> EndCombat(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        var combatId = _keys.CombatCurrent(effective);

        return ExecuteAsync(async session =>
        {
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounter>(false, Error: "NotFound", Summary: "No active combat encounter to end.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);
            var expiredMessages = new List<string>();

            // Clear all round-based status effects when combat ends.
            // This implements "until end of combat" semantics for effects created with ExpiresAtRound.
            // Day-based effects (ExpiresAtDay) are handled separately by StatusExpiryRule during advance_world.
            // Note: This is intentionally aggressive — all round-tied effects are removed on combat end.
            foreach (var character in characters.Values.Where(c => c != null))
            {
                if (character.SystemStats?.StatusEffects != null)
                {
                    var effects = character.SystemStats.StatusEffects;
                    var toRemove = effects.Where(e => e.ExpiresAtRound.HasValue).ToList();
                    foreach (var effect in toRemove)
                    {
                        effects.Remove(effect);
                        expiredMessages.Add($"Cleared effect '{effect.Name}' on '{character.Name}'.");
                    }
                }
            }

            encounter.IsActive = false;
            encounter.ActiveTurnId = null;

            await session.StoreAsync(encounter, encounter.Id);

            var summary = "Combat encounter ended.";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    // --- Dedicated Campaign Management Tools ---

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Creates a new campaign with a name and initial ruleset.
The ruleset is immediately locked for this campaign, preventing accidental system changes later.
Automatically selects the newly created campaign as the current one.

Example: create_campaign(""dragonheist"", RulesetSystem.Dnd5e, ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description("Unique name/slug for the campaign (e.g. 'dragonheist', 'curse-of-strahd').")] string name,
        [Description("Initial ruleset system. This will be locked.")] RulesetSystem initialSystem,
        [Description("Optional human-friendly display name.")] string? displayName = null)
    {
        var normalized = name.Trim().ToLowerInvariant();

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);
            if (existing != null)
            {
                return new ToolResult<Campaign>(false, Error: "AlreadyExists", Summary: $"Campaign '{normalized}' already exists.");
            }

            var campaign = await GetOrCreateCampaignMetaAsync(session, normalized, initialSystem, displayName, forceLock: true);

            // Select it immediately for convenience
            _currentCampaign.SetCurrent(normalized);

            return new ToolResult<Campaign>(true, campaign, $"Campaign '{normalized}' created and locked to {initialSystem}. Now selected as current.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Lists all existing campaigns in the database.
Useful for discovering existing worlds to join before calling select_campaign.")]
    public Task<ToolResult<List<Campaign>>> ListCampaigns()
    {
        return ExecuteAsync(async session =>
        {
            // Query all Campaign documents (they live under campaigns/*/meta)
            var campaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/"))
                .ToListAsync();

            return new ToolResult<List<Campaign>>(true, campaigns, $"Found {campaigns.Count} campaign(s).");
        }, saveChanges: false);
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Selects a campaign as the current one for this session.
Most tools will use this campaign context automatically, meaning you don't need to specify 'campaignName' on subsequent tool calls.

Example: select_campaign(""dragonheist"")")]
    public Task<ToolResult<string>> SelectCampaign(
        [Description("Name of the campaign to select.")] string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return Task.FromResult(new ToolResult<string>(false, Error: "InvalidArgument", Summary: "campaignName is required."));
        }

        var normalized = campaignName.Trim().ToLowerInvariant();
        _currentCampaign.SetCurrent(normalized);

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);

            if (existing == null)
            {
                // Auto-create a minimal campaign entry so lock-in and per-campaign state can work
                await GetOrCreateCampaignMetaAsync(session, normalized, RulesetSystem.Dnd5e, forceLock: false);

                return new ToolResult<string>(true, normalized, 
                    $"Campaign '{normalized}' selected (new minimal campaign created with D&D 5e as default system).");
            }

            return new ToolResult<string>(true, normalized, $"Campaign '{normalized}' is now selected as current.");
        });
    }

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN DISCOVERABILITY: Returns the currently active campaign context (name, lock-in status, and active ruleset).
Use this if you are unsure which campaign you are currently in or if you need to know the active ruleset system (e.g., Dnd5e, Pf2e) before using ruleset_actions in combat.")]
    public Task<ToolResult<Campaign>> GetCurrentCampaign()
    {
        var effective = EffectiveCampaign(null);
        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(campaignId);
            if (campaign == null)
            {
                return new ToolResult<Campaign>(false, Error: "NotFound", Summary: $"Campaign '{effective}' meta document not found. The campaign might not be initialized yet.");
            }

            return new ToolResult<Campaign>(true, campaign, $"Currently selected campaign: {effective}");
        }, saveChanges: false);
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"TOOL CATALOG: Returns the complete list of CampaignVault MCP tools (name, category, one-line description). Call this if search-based discovery only surfaced a subset. Optional category filter available.")]
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(
        [Description("Optional category filter. Omit to return all tools. Values: Session & exploration, Mutation & time, Combat & rulesets, Campaign management, Deep dives, World builder, System.")] string? category = null)
    {
        var tools = ToolCatalog.GetByCategory(category);
        var summary = string.IsNullOrWhiteSpace(category)
            ? $"Returned {tools.Count} tools across all categories. Call get_help for usage patterns."
            : $"Returned {tools.Count} tools in category '{category.Trim()}'.";
        return Task.FromResult(new ToolResult<IReadOnlyList<ToolCatalogEntry>>(true, tools, summary));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: CALL THIS FIRST. Returns the canonical DM manual with quickstart, tool index, copy-paste commit patterns, ruleset_actions, StatusEffects, and WorldPressure handling. Use list_tools for the full machine-readable catalog.")]
    public Task<ToolResult<string>> GetHelp()
    {
        var manual = @"# CampaignVault DM Manual

Welcome to the CampaignVault engine. Your role as the AI DM is to drive the narrative while letting the MCP engine handle the persistence, math, and simulation.

## Quickstart for Models
1. **Call `get_help`** (this document) and **`list_tools`** if search-based discovery only showed a subset.
2. **Call `get_current_campaign`** or **`create_campaign`** / **`select_campaign`** to establish campaign context.
3. **Call `get_world_state`** at session start to sync time, rumors, events, and **WorldPressure**.
4. **Call `get_scene`** whenever the party enters a location. Action any `ENGINE WARNING` / `NARRATIVE PROMPT` immediately.
5. **Call `commit`** at the end of every meaningful beat (combat, conversation, discovery, persistence).
6. **Call `advance_world`** for travel, rests, or downtime skips.

## Tool Index by Category

### Session & exploration
| Tool | Purpose |
|------|---------|
| `get_current_campaign` | Active campaign name, ruleset, lock-in status |
| `get_world_state` | Session kickoff: time, rumors, recent events, pressures |
| `get_scene` | Location, NPCs, items, rumors, ActiveCombat, SystemStats, pressures |
| `get_npc_context` | Deep NPC psychology, memories, initiative signals |
| `get_party` | Retrieve all PCs and major KeepAlive characters |
| `get_npc_needs` | Current needs + merged descriptors |
| `get_need_descriptors` | Per-campaign shared need descriptions |
| `search_world` | Keyword search across lore, characters, locations |
| `recall_history` | Keyword search over past event summaries |
| `get_help` | Built-in DM manual and copy-paste patterns |
| `list_tools` | Full machine-readable tool catalog |

### Mutation & time
| Tool | Purpose |
|------|---------|
| `commit` | Universal atomic write (`WorldChange[]` with `$type` discriminators) |
| `advance_world` | Fast-forward days, run simulation rules, return pressures |

### Combat & rulesets
| Tool | Purpose |
|------|---------|
| `get_config` / `set_active_system` | Read or set active ruleset (D&D 5e, PF2e, Fallout 2d20) |
| `start_combat` / `next_turn` / `end_combat` | Initiative at start, turn tracking, round-based status expiry |

### Campaign management
| Tool | Purpose |
|------|---------|
| `create_campaign` / `list_campaigns` / `select_campaign` | Create, list, and activate campaigns |

### Deep dives
| Tool | Purpose |
|------|---------|
| `get_faction_context` | Full faction document (stances, territory, EconomicDemand) |
| `get_quest_details` | Full quest document (objectives, deadlines, progress timestamps) |

### World builder
| Tool | Purpose |
|------|---------|
| `upsert_character` / `upsert_location` / `upsert_lore` | Initial seeding and major structural work |
| `define_need_descriptor` | Per-campaign shared need descriptions |

**During play, strongly prefer `commit` (especially `activity` changes) over world-builder upserts.**

**KEY PHILOSOPHY (Anti-LLM-Laziness / Schrödinger's World):** 95%+ of the world is ephemeral flavor that lives ONLY in your current narration/context. Only *meaningful* interactions (that will be referenced again, combat, theft, named recurring NPCs, discovered secret doors the party will use) should be anchored via `commit`. The engine owns linking, GC of transients, visit tracking, and nags you *immediately* on the next `get_scene` or `get_world_state` with **exact, copy-paste-ready JSON** when you (or prior LLM turns) were lazy/incomplete. Treat every string in `WorldPressure` that starts with `ENGINE WARNING:` or `NARRATIVE PROMPT:` as a **mandatory high-priority directive**. Paste the example JSON into your next `commit` call. This defeats the ""silly factor"" of being forced to output perfect polymorphic arrays for every tavern bard or crate.

## Core Gameplay Loop
1. **Start of Session**: Call `get_current_campaign` + `get_world_state` (with party location) to sync time, rumors, events, char distress, **and WorldPressure**.
2. **Exploration**: Call `get_scene` on entry. **Immediately action any ENGINE WARNING / NARRATIVE PROMPT in the WorldPressure** (use the exact JSON provided).
3. **Action & Consequence**: Narrate vividly to players. At end of beat (or when something should persist), call `commit` with array of changes. Use `activity` liberally to keep sim in sync.
4. **Time Skips / Travel**: `advance_world` (triggers needs, rumor decay, schedule eval, **TransientEvictionRule** for flavor NPCs).
5. **Deep NPC**: `get_npc_context` + `get_npc_needs`.

**Golden Rule:** If you just narrated something that should ""exist"" next time the party returns or is referenced, `commit` it (via create or update). If it's pure color, use PointsOfInterest + AmbientCrowd (lightweight, no docs created until you decide to promote).

## The Commit Tool (Universal Write)
ALWAYS call at end of combat/conversation/discovery. Atomic array of `$type` mutations. Mutations are processed atomically as a single database transaction. 

- **Batch Size Guidance:** Individual commits are capped at a maximum of **50 changes** per call. Group all related mutations (e.g. travel, quest progress, HP updates, and activity updates) into a single batch to ensure consistency.
- **ID Hygiene & Campaign Isolation:** To prevent ID collisions and cross-campaign data leakage, **always namespace your entity IDs** with a unique campaign prefix/slug (e.g., `locations/dragonheist-trollskull-alley`, `chars/dragonheist-volo` instead of `locations/starting-tavern`, `chars/bard`).

Supported `$type`s: `hp`, `item`, `item_update`, `status`, `statusremove`, `event`, `rumor`, `relationship`, `engagement_relation`, `spatial_position`, `need`, `attribute`, `mood`, `activity`, `ruleset_action`, `location_create`, `location_update`, `character_create`, `character_update`, `system_stats`, `knowledge_update`, `schedule_change`, `item_create`, `travel`, `rest`, `faction_create`, `faction_reputation`, `faction_state`, `quest_create`, `quest_progress`.

**Travel and Resting:** Use `travel` (with `destinationLocationId`) to safely move the party; it applies time and tiredness, and evaluates encounters based on distance. Use `rest` (with `intendedHours` and `securityModifier`) for camping or sleeping. The engine rolls for interruptions. If `rest` is interrupted, resolve the encounter before committing `hp` recovery!

**RECOMMENDED PATTERNS (copy-paste and adapt):**

Basic update + sync:
[
  { ""$type"": ""event"", ""category"": ""Narrative"", ""summary"": ""Party found the hidden stair."" },
  { ""$type"": ""activity"", ""characterId"": ""chars/guard1"", ""newLocationId"": ""locations/cellar"", ""newActivity"": ""Searching crates nervously"" }
]

**Creating on the fly (the laziness countermeasure - use these instead of pure narration for anything that might matter later):**
[
  { ""$type"": ""location_create"", ""locationId"": ""locations/tavern_cellar"", ""name"": ""Dank Cellar"", ""description"": ""Smells of damp earth..."", ""type"": ""Room"", ""connectedFromLocationId"": ""locations/tavern"", ""connectionDescription"": ""A wooden trapdoor leading down"", ""pointsOfInterest"": [""Suspicious crate"", ""Rat gnawing bone""], ""ambientCrowd"": ""2-3 rats and a drunk sleeping it off"" },
  { ""$type"": ""character_create"", ""characterId"": ""chars/cloaked_figure"", ""name"": ""Cloaked Figure"", ""currentLocationId"": ""locations/tavern_cellar"", ""currentActivity"": ""Watching the party"", ""keepAlive"": false, ""notes"": ""Offered a map for coin."" }
]

Later promote a transient (so it survives GC and participates in AdvanceWorld):
[
  { ""$type"": ""schedule_change"", ""characterId"": ""chars/cloaked_figure"", ""schedule"": { ""defaultLocationId"": ""locations/market_square"", ""routines"": [ { ""condition"": ""Any"", ""locationId"": ""locations/market_square"", ""activity"": ""Haggling"", ""probability"": 0.8 } ] } }
]

**Engagements & Spatial Positions:** pairwise state (`engagement_relation`) vs. relative placement (`spatial_position`). Different field names: `actorId` vs `characterId`.

Categories for `engagement_relation`: `Physical`, `Social`, `Medical`, `Attention`, `Proximity`. Use a freeform `verb` (e.g. ""grappling"", ""ranting at"", ""stitching""). Omit `restrictionLevel` to use category defaults — Physical/Medical = Hard (blocks `travel` + scene pressure), Social = Soft (pressure only), Attention/Proximity = None (informational). Override with `restrictionLevel` when a beat must hard-lock travel (e.g. farewell embrace).

`distanceBand` values: `Touch`, `Close`, `Near`, `Far`, `Distant`. Optional `bearing` and `zone`.

Tavern example (drunk five paces from the party, ranting):
[
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": ""Near"", ""zone"": ""bar"" },
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""category"": ""Social"", ""verb"": ""ranting at"", ""bidirectional"": true }
]

Farewell embrace (hard-lock until resolved — override Social default):
[
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/mother"", ""targetId"": ""chars/son"", ""category"": ""Social"", ""verb"": ""embracing"", ""restrictionLevel"": ""Hard"", ""bidirectional"": true }
]

Clear when the beat ends (`verb` or `distanceBand` null):
[
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/mother"", ""targetId"": ""chars/son"", ""verb"": null, ""bidirectional"": true },
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": null }
]

*Combat vs manual: ruleset resolvers automatically establish and clear mechanical engagements (grappling, escape) via `ruleset_action` contested checks. For unresolved non-combat beats (hugs, tending wounds, intense confrontations), commit `engagement_relation` yourself — otherwise scene pressure will nag you and Hard engagements block `travel`.*

Item + transfer patterns, status with modifiers, ruleset_action (see below), etc.

**After you see a pressure in get_scene/get_world_state, your *next* action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names).** Then narrate the outcome. The engine will clear the pressure on subsequent reads.

## Schrödinger's World + Transient / Open-World Patterns (Critical for Laziness Mitigation)
- **Flavor without bloat**: When narrating a crowded tavern, a bustling market, rats in a cellar, or ""a bard playing a lute in the corner"", **do not** immediately `character_create` 20 people. Instead:
  - On initial `location_create` or via `location_update`: populate `pointsOfInterest` (light list of strings returned in get_scene) and/or `ambientCrowd` (string hint, e.g. ""8-15 rough sailors and dockworkers"").
  - The engine will surface a `NARRATIVE PROMPT` in get_scene when the live scene is empty but ambient is expected: this is your cue to spawn 1-3 *interactable* transients via `character_create` if players engage, or just narrate using the hint.
- **Transients auto-GC**: Any character created (or moved via activity) with `schedule: null` AND `keepAlive: false` is transient. When the party leaves the area (get_scene on another loc + `advance_world` days later) and `LastVisitedDay` on the loc is old (>1 day), the `TransientEvictionRule` emits `ActivityChange` deltas that clear `CurrentLocationId`. The doc stays (cheap) for possible later promotion by ID or narrative callback. Use `keepAlive: true` for PCs, companions, or ""favorite"" flavor you want to keep without a full schedule.
- **Auto-Linking prevents soft-locks**: Always supply `connectedFromLocationId` + `connectionDescription` on `location_create`. Engine appends forward + reverse exits (and sets parent). If you forget, next get_scene on the child will give ENGINE WARNING + exact `location_update` JSON to add the missing exit.
- **Promotion path**: Use `schedule_change` (or supply schedule at `character_create` time) to make a transient permanent (it now runs in simulation, ignored by GC).
- **Dead-ends / broken maps**: get_scene will nag with ready `location_update` + `addExit`. Use it.
- **Hallucinated locations**: get_scene never throws for bad ID. Returns stub + strong ENGINE WARNING with ready `location_create` JSON (including connectedFrom suggestion). Paste it.

**Full ""Lazy LLM Tavern"" Walkthrough Example (copy this pattern):**
You (LLM): ""You push open the door to the Rusty Nail. The common room is full of sailors and dockworkers. A one-eyed bard in the corner is singing a shanty about lost ships while plucking a battered lute. The air smells of salt, sweat, and cheap ale. A toothless barman named Bram wipes a mug...""

(You used ambient flavor + PoIs implicitly via narration. No commit yet - correct for pure color.)

Later, party talks to the bard or barman engages:
- Call `get_scene ""locations/rusty-nail""` first (authoritative state).
- Suppose it returns empty PresentNPCs but AmbientCrowd hint (or prior you set none) + NARRATIVE PROMPT pressure: it will literally give you the JSON array.
- Then: `commit` the create for the interactable ones only:
  [
    { ""$type"": ""character_create"", ""characterId"": ""chars/bram-the-barkeep"", ""name"": ""Bram Ironarm"", ""currentLocationId"": ""locations/rusty-nail"", ""currentActivity"": ""Wiping mugs and watching the door"", ""notes"": ""Toothless, one good eye, ex-sailor. Knows harbor gossip."", ""psychology"": { ""wants"": [""quiet night"", ""coin""], ""fears"": [""trouble in his bar""] } },
    { ""$type"": ""character_create"", ""characterId"": ""chars/one-eyed-bard"", ... similar ... },
    { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party met Bram and the bard at the Rusty Nail."" }
  ] ""The party enters and interacts with the locals.""

- If later the bard becomes a quest giver recurring: `schedule_change` or add Schedule at birth + `keepAlive`.
- If they just drink and leave: no commit needed for the 12 unnamed sailors. Engine will GC any you did transiently create if area goes cold.

**Full ""Travel, Faction, Quest & Rumor"" Batch Example (Cohesive World Beats):**
When the party resolves a rumor about a rebel smuggler by betraying them to the city watch, batch all the consequences:
[
  { ""$type"": ""travel"", ""characterId"": ""chars/pc1"", ""destinationLocationId"": ""locations/city-jail"", ""encounterRiskModifier"": -30 },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/betray-smuggler"", ""objectiveIndex"": 0, ""newState"": ""Complete"", ""narrativeNote"": ""Handed the rebel smuggler over to the City Watch."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 15 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/rebels"", ""characterId"": ""chars/pc1"", ""delta"": -20 },
  { ""$type"": ""rumor"", ""subject"": ""smuggling"", ""newText"": ""The smuggler who supplied the rebels was caught and jailed."", ""newState"": ""Resolved"" },
  { ""$type"": ""character_update"", ""characterId"": ""chars/smuggler-npc"", ""keepAlive"": true },
  { ""$type"": ""activity"", ""characterId"": ""chars/smuggler-npc"", ""newLocationId"": ""locations/city-jail"", ""newActivity"": ""Imprisoned behind iron bars"" },
  { ""$type"": ""event"", ""category"": ""Narrative"", ""summary"": ""Party betrayed the rebel smuggler at the city gate; smuggler is now locked up."" }
]
This safely moves the party (with time + fatigue), updates the quest, modifies standing with two factions, resolves the active rumor, moves the smuggler NPC into jail with a new activity, and logs a narrative event in a single atomic database operation.

This is how you stay creative *and* keep the world model healthy without perfect JSON for every flavor element.

**Full ""Quest + Faction + Rumor Lifecycle"" Walkthrough (how a narrative thread breathes across multiple sessions):**

A complete arc — from seeded rumor through investigation, faction reaction, and resolution — spans several commits. Here is the canonical pattern. Adapt IDs to your campaign prefix.

**Beat 1 — Seed the thread (tavern, session start):**
Bram the barkeep mentions the Nightshade gang has been raiding river barges. Commit the rumor and the quest hook, and flag Bram as the quest giver:
[
  { ""$type"": ""rumor"", ""subject"": ""Nightshade Gang"", ""newText"": ""Nightshade pirates have raided three barges on the Ashford River this month — cargo vanishing, crews turning up dead."", ""newState"": ""Active"", ""sourceCharacterId"": ""chars/bram-the-barkeep"" },
  { ""$type"": ""quest_create"", ""questId"": ""quests/stop-nightshade"", ""title"": ""Cut Out the Nightshade"", ""description"": ""The river merchants are desperate. Find and disrupt the Nightshade Gang's operations on the Ashford."", ""objectives"": [ { ""description"": ""Locate the Nightshade hideout"", ""state"": ""Active"" }, { ""description"": ""Destroy or scatter the gang"", ""state"": ""Pending"" }, { ""description"": ""Report back to the River Merchants' Guild"", ""state"": ""Pending"" } ], ""deadlineDays"": 14 },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Bram Ironarm told the party about the Nightshade Gang's river raids. Quest: Cut Out the Nightshade accepted."" }
]

**Beat 2 — Investigation (party scouting the docks):**
Party discovers the gang uses a hidden canal warehouse. Create the location, advance the quest, record the discovery:
[
  { ""$type"": ""location_create"", ""locationId"": ""locations/nightshade-warehouse"", ""name"": ""Nightshade Canal Warehouse"", ""description"": ""A damp, low-ceilinged warehouse reachable only by flat-bottomed barge. Crates of stolen cargo line the walls."", ""type"": ""Building"", ""connectedFromLocationId"": ""locations/ashford-docks"", ""connectionDescription"": ""A concealed canal lock, invisible at high tide"" },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 0, ""newState"": ""Complete"", ""narrativeNote"": ""Party located the warehouse via the canal lock at low tide."" },
  { ""$type"": ""knowledge_update"", ""characterId"": ""chars/pc1"", ""topic"": ""Nightshade Gang"", ""details"": ""Hideout is the canal warehouse south of Ashford Docks, accessible only at low tide."" },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party found the Nightshade Gang hideout: a canal warehouse south of Ashford Docks."" }
]

**Beat 3 — Confrontation + faction ripple (the gang is broken):**
Party raids the warehouse, kills the gang leader, frees hostages. Faction standing shifts:
[
  { ""$type"": ""hp"", ""characterId"": ""chars/nightshade-boss"", ""delta"": -99, ""sourceCharacterId"": ""chars/pc1"" },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 1, ""newState"": ""Complete"", ""narrativeNote"": ""Gang leader slain; surviving members fled or surrendered."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/nightshade-gang"", ""influenceDelta"": -30, ""narrative"": ""Leadership killed in the warehouse raid. Gang scattered."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/river-merchants-guild"", ""characterId"": ""chars/pc1"", ""delta"": 20 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 8 },
  { ""$type"": ""rumor"", ""subject"": ""Nightshade Gang"", ""newText"": ""The Nightshade pirates were smashed by a band of adventurers at their own hideout. The river may be safe again."", ""newState"": ""Resolved"" },
  { ""$type"": ""event"", ""category"": ""Combat"", ""summary"": ""Party raided the Nightshade warehouse. Boss killed, gang scattered. River Merchants Guild grateful."" }
]

**Beat 4 — Resolution + world state shift (report to the guild):**
Party reports back. Quest closes, territory adjusts, maybe a new rumor seeds:
[
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 2, ""newState"": ""Complete"", ""narrativeNote"": ""Party reported to the River Merchants Guild. Reward collected."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/river-merchants-guild"", ""influenceDelta"": 10, ""narrative"": ""Guild influence rising now the river route is open; trade caravans resuming."" },
  { ""$type"": ""rumor"", ""subject"": ""Ashford River"", ""newText"": ""Merchants are saying the Ashford route is profitable again. Caravans are reforming for the first time in weeks."", ""newState"": ""Active"" },
  { ""$type"": ""event"", ""category"": ""Narrative"", ""summary"": ""Quest complete. River Merchants Guild paid the reward. Trade caravans reforming on the Ashford."" }
]

After Beat 4: `get_world_state` will show the quest as resolved, both factions at updated standing, the original rumor as Resolved (no longer nagging), and a new active rumor seeding the next hook. Faction pressure contributors will start surfacing new opportunistic moves from the now-stronger River Merchants Guild if their influence crossed the threshold. The engine does the bookkeeping; you drive the story.

**KEY PATTERNS from this arc:**
- One rumor → one quest → multiple quest_progress commits (one per objective). Never skip objectives.
- Faction rep + faction_state are separate: `faction_reputation` is per-character standing; `faction_state` is the global influence/territory of the faction itself. Both should shift after major events.
- Always resolve the rumor when the quest closes — they are linked narratively but not auto-linked technically. Forgotten rumors age into pressure nagging.
- New rumors seed naturally from consequences. That last rumor about caravans is tomorrow's quest hook.
- `knowledge_update` on key discoveries gives the character something to ""remember"" that decays over time — pressure will remind you to refresh it if the info goes stale.

**Character Combat Bootstrap — required for all combatants (KeepAlive OR maxHp > 0):**
The engine emits ENGINE WARNING until BOTH are set:
1. **HP**: `maxHp` (+ optional `currentHp`)
2. **systemStats**: ruleset-specific combat stats via `systemStats` on `character_create` or `system_stats` patch

D&D 5e reference (level 1, max hit die + CON modifier):
- Fighter / Paladin / Ranger: d10 → 10 + CON mod
- Cleric / Druid / Monk / Warlock / Bard: d8 → 8 + CON mod
- Rogue / Artificer: d8 → 8 + CON mod
- Wizard / Sorcerer: d6 → 6 + CON mod
- Barbarian: d12 → 12 + CON mod

For NPCs/creatures: use the stat block value (e.g. Goblin = 7 HP, AC 15, DEX 14).
Infer from class+level for PCs. Pure flavor transients (no HP, not KeepAlive) skip this.

Full 5e bootstrap at create:
{ ""$type"": ""character_create"", ""characterId"": ""chars/goblin-scout"", ""name"": ""Goblin Scout"", ""maxHp"": 7, ""currentHp"": 7, ""classLevel"": ""Goblin 1"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 15, ""dexterity"": 14, ""strength"": 8, ""skillModifiers"": { ""Stealth"": 6, ""Perception"": 2 }, ""savingThrowModifiers"": { ""Dexterity"": 2 } } }

PF2e bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/level2-fighter"", ""name"": ""Elara"", ""keepAlive"": true, ""maxHp"": 32, ""currentHp"": 32, ""classLevel"": ""Human Fighter 2"", ""systemStats"": { ""$system"": ""pf2e"", ""armorClass"": 19, ""strengthMod"": 4, ""dexterityMod"": 2, ""skillModifiers"": { ""Perception"": 8, ""Athletics"": 9 }, ""savingThrowModifiers"": { ""Fortitude"": 9, ""Reflex"": 7, ""Will"": 6 } } }

Fallout 2d20 bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/raider"", ""name"": ""Raider"", ""maxHp"": 10, ""currentHp"": 10, ""systemStats"": { ""$system"": ""fallout2d20"", ""agility"": 7, ""perception"": 6, ""endurance"": 5, ""defense"": 1, ""skills"": { ""SmallGuns"": 2 }, ""tagSkills"": [""SmallGuns""] } }

Patch stats on existing character:
{ ""$type"": ""system_stats"", ""characterId"": ""chars/campaign-thorin"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 16, ""strength"": 16, ""skillModifiers"": { ""Athletics"": 5 } } }

**The Visual / Physics Sandbox (Tags & Appearance) & Knowledge:**
The engine intentionally avoids hardcoding vulnerability scores or mechanical checks for narrative states like ""wet"" or ""disheveled"". You (the LLM) are the physics engine.
- Use `$type: ""item_create""` with `coreCategory` (e.g., ""Weapon"", ""Armor"", ""Document"") when looting or discovering items. Set `holderId` to a PC character ID (or ""party"") for inventory.
- Use `$type: ""item_update""` to add temporary `TagsToAdd` (e.g., `[""wet"", ""muddy""]`) and a narrative `NewState` (e.g., ""Covered in mud"") to items. You can also add permanent `FeaturesToAdd` (e.g., ""Leather wrapped handle"") or change `coreCategory`.
- Use `$type: ""character_update""` to do the same for characters. Give them temporary `TagsToAdd` (`[""soot_covered""]`), narrative `AppearanceOverride`, or permanent `FeaturesToAdd` (`[""Scar over left eye""]`).
- Use `$type: ""location_update""` with `newState`, `tagsToAdd`, and `featuresToAdd` to persistently change the environment (e.g., ""On fire"", `[""smoky""]`, `[""collapsed roof""]`).
- Use `$type: ""knowledge_update""` to record an important memory for a character (e.g., `""topic"": ""The Dragon"", ""details"": ""Lives in the mountain.""`). Memories naturally decay and generate prompt pressure over time to simulate epistemic drift!
- Read these fields from `SceneView` and interpret them naturally. If a goblin has the ""wet"" tag, you inherently know lightning magic should be more effective. If the PC is ""disheveled"", the noble faction should react poorly.
- Factions have dynamic `EconomicDemand`. If a faction is desperate for an item the party is carrying (e.g. ""spell scrolls""), `get_scene` will pressure you to narrate merchants offering a premium or thieves attempting to steal them. Fulfill this naturally during roleplay!

## Ruleset Actions (Combat & Skill Checks)
... (same as before, keep the examples)

## Status Effects & Stat Modifiers
... (same)

## Phase 7.4 Deep Dives & Suggested Commits
If a scene has `ActiveQuests` or `RelevantFactions`, you can explore them directly via:
- `get_quest_details`: Read the full Quest structure (all objectives, deadlines, rewards).
- `get_faction_context`: Get the full Faction summary, stances, territory, and influence.
Also, if `get_scene` or `get_world_state` returns `SuggestedCommitExamples` array, copy-paste one directly into your `commit` tool (examples frequently contain real IDs from the current state; replace any remaining placeholders like `locations/actual-dest` if needed) to easily resolve stuck characters or progress quests.

## World Pressure (Your Co-DM Nag System)
Pressures appear in **every** `get_world_state`, `get_scene`, and `advance_world` response (in the ToolResult.WorldPressure array, and also embedded in some views).

- `ENGINE WARNING`: Structural/integrity problem (hallucinated loc, no exits, broken link, etc.). **Paste the JSON and fix immediately.** These are the primary defense against laziness and broken worlds.
- `NARRATIVE PROMPT`: Opportunity / flavor cue (empty but ambient expected, no PoIs on a lively spot). Use to decide whether to persist something or just narrate using the hint.
- Simulation / character / rumor pressures: Aging unresolved, dying PCs/NPCs, desperate needs, etc. Many now include mini example commit snippets.

**Never ignore them.** The next `get_scene` after you fix will usually have fewer or none. If you keep seeing the same one, you skipped the commit.

Additional pressures come from character distress contributors (HP, bad statuses, high needs) surfaced via get_world_state, plus rule narratives turned into SimulatorEvents on advance.

## Other Tools & Patterns
- `get_npc_context` / `get_npc_needs`: Use before deep roleplay. Merge descriptors happen automatically.
- `search_world`, `recall_history`: For discovery without hallucinating duplicates.
- `define_need_descriptor` + `get_need_descriptors`: For custom needs vocabulary (wanderlust, debt_pressure, etc.).
- World-builder upserts: Fine for initial seeding / major PoIs. During play, prefer `commit` + the runtime creates.
- Combat: start_combat, next_turn, end_combat + ruleset_action inside commit. Statuses applied via commit survive and modify future rolls.

## Common Laziness Traps & How the Engine Helps
- Narrating a whole new dungeon level without creates -> next get_scene on a room ID: instant hallucination pressure + exact create JSON.
- Creating a cellar via create but forgetting the back exit -> pressure on entry.
- Spawning 40 named sailors for one scene -> bloat; use ambient + 1-2 creates only for interactables; GC cleans the rest.
- Forgetting to `activity` change after a scene -> get_scene shows stale locations/activities.
- Ignoring an aging ""Unresolved"" event for 10 days -> pressure in get_world_state with resolution hint.

Call `get_help` any time you (the LLM) are unsure. Re-read the pressures section often.

Remember: the engine is strict on invariants (map connectivity, no silent deletes of important state) so *you* can be creatively lazy about flavor.
";
        return Task.FromResult(new ToolResult<string>(true, manual, "Help manual retrieved."));
    }

}

