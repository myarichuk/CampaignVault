using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;
using Microsoft.Extensions.Logging;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Rulesets;

namespace CampaignVault.Data;

public class CampaignRepository
{
    private readonly IDocumentStore _store;
    private readonly IWorldSimulationEngine _simulationEngine;
    private readonly ILogger<CampaignRepository> _logger;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly WorldChangeDispatcher _changeDispatcher;
    private readonly CampaignDocumentKeys _keys;
    private readonly ICurrentCampaignContext? _currentCampaign;

    private string ResolveCampaign(string? campaignName)
    {
        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            return campaignName;
        }

        if (!string.IsNullOrWhiteSpace(_currentCampaign?.CurrentCampaignName))
        {
            return _currentCampaign.CurrentCampaignName;
        }

        return "default";
    }

    public CampaignRepository(
        IDocumentStore store, 
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext? currentCampaign = null,
        IEnumerable<IWorldChangeHandler>? changeHandlers = null)
    {
        _store = store;
        _simulationEngine = simulationEngine;
        _logger = logger;
        _behaviorSynthesizer = behaviorSynthesizer;
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign;

        var handlersList = (changeHandlers ?? []).ToList();

        if (handlersList.Count == 0) //TODO: consider what should be done here - this is brittle as fuk
        {
            // Default to full production handler set so simulation tests and legacy 4-arg constructions continue to work
            handlersList =
            [
                new HpChangeHandler(),
                new ItemTransferHandler(),
                new StatusChangeHandler(),
                new EventOccurredHandler(),
                new RumorEvolvesHandler(),
                new RelationshipChangeHandler(),
                new NeedChangeHandler(),
                new AttributeChangeHandler(),
                new MoodChangeHandler(),
                new ActivityChangeHandler(),
                new LocationCreateHandler(),
                new LocationUpdateHandler(),
                new CharacterCreateHandler(),
                new ItemCreateHandler(),
                new ScheduleChangeHandler(),
                new TravelChangeHandler(),
                new FactionReputationChangeHandler(),
                new FactionStateChangeHandler(),
                new QuestCreateHandler(),
                new QuestProgressHandler(),
                new FactionCreateHandler(),
                new ItemUpdateHandler(),
                new CharacterUpdateHandler(),
                new KnowledgeUpdateHandler(),
                new RulesetActionHandler(
                    new RulesetResolverSelector([
                        new Dnd5eRulesetResolver(new DefaultRollService()),
                        new Pf2eRulesetResolver(new DefaultRollService()),
                        new Fallout2d20RulesetResolver(new DefaultRollService())
                    ]),
                    new CampaignDocumentKeys(),
                    currentCampaign ?? new CurrentCampaignContext()),
                new RumorCreateHandler(),
                new RestChangeHandler()
            ];
        }

        _changeDispatcher = new(handlersList, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldChangeDispatcher>.Instance);
    }

    /// <summary>
    /// Convenience constructor primarily for test scenarios.
    /// Supplies the full set of production handlers so the legacy fallback can be removed.
    /// </summary>
    public CampaignRepository(IDocumentStore store)
        : this(store, 
               new NoOpSimulationEngine(), 
               Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
               new DefaultBehaviorSynthesizer(),
               new(),
               currentCampaign: null)
    {
    }

    /// <summary>
    /// Legacy 4-argument constructor used extensively by existing tests.
    /// Supplies modern defaults for CampaignDocumentKeys and ICurrentCampaignContext so tests continue to compile
    /// after the multi-campaign / deep-propagation changes without requiring mass updates to test setup code.
    /// </summary>
    public CampaignRepository(
        IDocumentStore store,
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer)
        : this(store, simulationEngine, logger, behaviorSynthesizer, new(), currentCampaign: null)
    {
    }

    /// <summary>
    /// Minimal no-op implementation so existing tests that do not care about simulation behavior continue to compile.
    /// AdvanceWorld tests will still need to construct a real engine (or we will update them in verification phase).
    /// </summary>
    private sealed class NoOpSimulationEngine : IWorldSimulationEngine
    {
        public Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default)
            => Task.FromResult(new SimulationResult([], [], []));
    }

    /// <summary>
    /// Opens an asynchronous document session with Optimistic Concurrency enabled.
    /// </summary>
    public IAsyncDocumentSession OpenSession()
    {
        var session = _store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }

    /// <summary>
    /// Stages a batch of WorldChange deltas into the provided session (applies clamping, atomic patches,
    /// relationship/need/attribute updates, etc.) and returns a summary.
    ///
    /// <para><b>Important:</b> This method does <b>not</b> call <c>SaveChangesAsync</c>. The caller
    /// (typically <c>CampaignTools.ExecuteAsync</c> or an explicit test block) is responsible for
    /// persisting. This keeps the method usable inside larger transactions and makes the contract explicit.</para>
    ///
    /// Use this for all atomic world mutations coming from tools or simulation rules.
    /// </summary>
    public async Task<CommitResult> StageChangesAsync(IAsyncDocumentSession session, WorldChange[] changes, string? campaignName = null)
    {
        changes ??= [];
        var effective = ResolveCampaign(campaignName);

        _logger.LogDebug("StageChangesAsync called with {ChangeCount} changes for campaign {Campaign}", changes.Length, effective);

        var result = await _changeDispatcher.DispatchAsync(
            session,
            changes,
            effective,
            () => GetTimeAsync(session, effective),
            async () => { var camp = await session.LoadAsync<Campaign>(_keys.Meta(effective)); return camp?.SystemOptions ?? new(); },
            ev => LogEventAsync(session, ev));

        if (result.Success && changes.Length > 0)
        {
            var metaId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(metaId);
            if (campaign != null)
            {
                var involvedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in changes)
                {
                    switch (c)
                    {
                        case HpChange hp:
                            involvedEntities.Add(hp.CharacterId);
                            break;
                        case StatusChange sc:
                            involvedEntities.Add(sc.CharacterId);
                            break;
                        case StatusRemove src:
                            involvedEntities.Add(src.CharacterId);
                            break;
                        case NeedChange nc:
                            involvedEntities.Add(nc.CharacterId);
                            break;
                        case FactionReputationChange frc:
                            involvedEntities.Add(frc.CharacterId); involvedEntities.Add(frc.FactionId);
                            break;
                        case LocationUpdate lu:
                            involvedEntities.Add(lu.LocationId);
                            break;
                        case RumorEvolves rc:
                            involvedEntities.Add(rc.RumorId);
                            break;
                        case EventOccurred { Involved: not null } ev:
                        {
                            foreach (var inv in ev.Involved) involvedEntities.Add(inv);
                            break;
                        }
                        case ActivityChange ac:
                            involvedEntities.Add(ac.CharacterId);
                            break;
                        case ScheduleChange shc:
                            involvedEntities.Add(shc.CharacterId);
                            break;
                        case ItemTransfer it:
                            involvedEntities.Add(it.ItemId); involvedEntities.Add(it.ToHolderId);
                            break;
                        case AttributeChange atc:
                            involvedEntities.Add(atc.CharacterId);
                            break;
                        case MoodChange mc:
                            involvedEntities.Add(mc.CharacterId);
                            break;
                        case RulesetAction ra:
                        {
                            involvedEntities.Add(ra.ActorId); 
                            foreach (var tid in ra.TargetIds) 
                                involvedEntities.Add(tid);

                            break;
                        }
                        case LocationCreate lc:
                            involvedEntities.Add(lc.LocationId);
                            break;
                        case CharacterCreate cc:
                        {
                            involvedEntities.Add(cc.CharacterId); if (cc.CurrentLocationId != null)
                            {
                                involvedEntities.Add(cc.CurrentLocationId);
                            }

                            break;
                        }
                        case ItemCreate ic:
                        {
                            involvedEntities.Add(ic.ItemId); 
                            involvedEntities.Add(ic.HolderId);
                            break;
                        }
                        case TravelChange tc:
                            involvedEntities.Add(tc.CharacterId); involvedEntities.Add(tc.DestinationLocationId);
                            break;
                        case RestChange restC:
                            involvedEntities.Add(restC.CharacterId); involvedEntities.Add(restC.LocationId);
                            break;
                        case FactionStateChange fsc:
                            involvedEntities.Add(fsc.FactionId);
                            break;
                        case QuestCreate qc:
                        {
                            involvedEntities.Add(qc.QuestId); 
                            if (qc.GiverId != null)
                            {
                                involvedEntities.Add(qc.GiverId);
                            }

                            foreach (var l in qc.RelatedLocationIds) involvedEntities.Add(l);
                            foreach (var f in qc.RelatedFactionIds) involvedEntities.Add(f);

                            break;
                        }
                        case QuestProgress qp:
                        {
                            involvedEntities.Add(qp.QuestId); 
                            if (qp.InvolvedIds != null)
                            {
                                foreach (var inv in qp.InvolvedIds) involvedEntities.Add(inv);
                            }

                            break;
                        }
                        case FactionCreate fc:
                            involvedEntities.Add(fc.FactionId);
                            break;
                        case RelationshipChange relc:
                            involvedEntities.Add(relc.SourceId); involvedEntities.Add(relc.TargetId);
                            break;
                    }
                }

                if (involvedEntities.Count > 0)
                {
                    var keysToRemove = campaign.PressureCooldowns.Keys
                        .Where(k => involvedEntities.Any(e => k.EndsWith($":{e}", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    foreach (var k in keysToRemove)
                    {
                        campaign.PressureCooldowns.Remove(k);
                    }
                }
                
                result.InvolvedEntities = involvedEntities.ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Scans characters in the current campaign for severe physical or psychological distress to surface as urgent narrative pressure.
    /// </summary>
    public async Task<List<WorldPressureItem>> GetCharacterPressureAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        // Hardened: filter by CampaignName (loose for shareable chars per design/feedback; strict would exclude legacy)
        var characters = await session.Query<Character>()
            .Where(c => string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective)
            .Where(c => c.KeepAlive)
            .Take(100)
            .ToListAsync();
        var pressure = new List<WorldPressureItem>();
        var badCategories = new[] { "Injury", "Condition", "Disease", "Poison", "Curse" };

        foreach (var c in characters)
        {
            if (c.CurrentHp <= c.MaxHp * 0.25f && c.CurrentHp > 0)
            {
                pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is critically wounded ({c.CurrentHp}/{c.MaxHp} HP).", "Character:CriticallyWounded"));
            }
            else if (c.CurrentHp <= 0)
            {
                pressure.Add(new(PressureSeverity.EngineWarning, c.Id, $"{c.Name} is dying or dead.", "Character:Dying"));
            }

            if (c.SystemStats?.StatusEffects != null)
            {
                foreach (var status in c.SystemStats.StatusEffects)
                {
                    if (badCategories.Contains(status.Category, StringComparer.OrdinalIgnoreCase))
                    {
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from {status.Name} ({status.Category ?? "Unknown"}).", $"Character:Status:{status.Name}"));
                    }
                }
            }

            foreach (var kvp in c.Needs.ActiveNeeds)
            {
                switch (kvp.Value)
                {
                    case > 80f:
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is in desperate need: {kvp.Key} ({kvp.Value:F0}%).", $"Character:Need:{kvp.Key}"));
                        break;
                    case > 50f:
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} needs should be acted upon: {kvp.Key} ({kvp.Value:F0}%).", $"Character:Need:{kvp.Key}"));
                        break;
                    case > 25f:
                        pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} start feeling the need: {kvp.Key} ({kvp.Value:F0}%).", $"Character:Need:{kvp.Key}"));
                        break;
                }
            }

            if (c.SystemStats != null)
            {
                if (c.SystemStats.Morale <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s morale is broken ({c.SystemStats.Morale:F0}%). Consider a breakdown, retreat, or refusal to fight.", "Character:Attribute:Morale"));
                }
                
                if (c.SystemStats.Willpower <= 10f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name}'s willpower is drained ({c.SystemStats.Willpower:F0}%). They are highly susceptible to manipulation, fear, or giving up.", "Character:Attribute:Willpower"));
                }

                if (c.SystemStats.Temperature <= -20f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is freezing to death ({c.SystemStats.Temperature:F0}). They should exhibit severe physical symptoms.", "Character:Attribute:TemperatureLow"));
                }
                else if (c.SystemStats.Temperature >= 50f)
                {
                    pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is suffering from extreme heat ({c.SystemStats.Temperature:F0}). They should exhibit exhaustion or heatstroke.", "Character:Attribute:TemperatureHigh"));
                }

                if (c.SystemStats.Attributes != null)
                {
                    foreach (var attr in c.SystemStats.Attributes)
                    {
                        var key = attr.Key.ToLowerInvariant();
                        if ((key == "corruption" || key == "fear" || key == "exhaustion") && attr.Value >= 90f)
                        {
                            pressure.Add(new(PressureSeverity.Simulation, c.Id, $"{c.Name} is consumed by {attr.Key} ({attr.Value:F0}). They should exhibit severe physical or mental symptoms.", $"Character:Attribute:{attr.Key}"));
                        }
                    }
                }
            }

            if (c.Social?.Relationships != null)
            {
                foreach (var rel in c.Social.Relationships)
                {
                    if (rel.Value <= -80)
                    {
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} actively despises '{rel.Key}' ({rel.Value} relationship). Their dialogue and actions towards them should be highly antagonistic or hostile.", $"Character:Relationship:{rel.Key}"));
                    }
                    else if (rel.Value >= 80)
                    {
                        pressure.Add(new(PressureSeverity.NarrativePrompt, c.Id, $"{c.Name} has deep trust and affection for '{rel.Key}' (+{rel.Value} relationship). They should act protective or highly agreeable towards them.", $"Character:Relationship:{rel.Key}"));
                    }
                }
            }
        }
        return pressure;
    }

    /// <summary>
    /// Fetches the synthesized state of a location, including NPCs present, visible items, local rumors, and recent events.
    /// This is the primary read operation used by the LLM when entering a new scene.
    /// </summary>
    public async Task<SceneView> GetSceneAsync(IAsyncDocumentSession session, string locationId, string? campaignName = null, bool markVisited = false)
    {
        var location = await session
            .Include<Location>(x => x.ParentLocationId)
            .LoadAsync<Location>(locationId);

        if (location == null)
        {
            // Per Phase 6 design: never throw on hallucinated location IDs.
            // Return a minimal stub so the tool layer can emit clear "use location_create" WorldPressure.
            // The stub is safe to return; callers should check IsLocationAnchored.
            var stub = new Location
            {
                Id = locationId,
                Name = "[Unanchored]",
                Description = "This location does not exist in the persistent world model yet.",
                Type = LocationType.Room,
                Exits = [],
                PointsOfInterest = [],
                AmbientCrowd = null,
                LastVisitedDay = null
            };
            return new()
            {
                Location = stub,
                PresentNPCs = [],
                LocalRumors = [],
                VisibleItems = [],
                RecentEvents = [],
                ActiveCombat = null,
                IsLocationAnchored = false,
                ActiveQuests = [],
                RelevantFactions = [],
                LastKnownTravel = null,
                SuggestedCommitExamples = []
            };
        }

        var effective = ResolveCampaign(campaignName);
        var regionId = location.ParentLocationId ?? locationId;
        var subLocations = (await QueryLocationsAsync(session, null, null, locationId, 20, effective)).ToList();

        var targetIds = new List<string> { locationId };
        targetIds.AddRange(subLocations.Select(l => l.Id));

        // Primary discovery via static schedule index (good for cold starts / world building)
        // Scoping hardened: filter by CampaignName (loose to allow shared NPCs/locations per design/feedback; strict for events/rumors).
        var npcsFromIndex = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            npcsFromIndex = npcsFromIndex.Where(n => string.IsNullOrEmpty(n.CampaignName) || n.CampaignName == effective).ToList();
        }

        // Fallback: if the index hasn't caught up (common in fast tests), load recent characters and filter client-side
        //TODO: add a warning log - so I can catch it in live instances
        if (npcsFromIndex.Count == 0)
        {
            var recentChars = await session.Query<Character>().Take(200).ToListAsync();
            if (!string.IsNullOrEmpty(effective))
            {
                recentChars = recentChars.Where(n => string.IsNullOrEmpty(n.CampaignName) || n.CampaignName == effective).ToList();
            }
            npcsFromIndex = recentChars
                .Where(x => x.Schedule != null &&
                            (targetIds.Contains(x.Schedule.DefaultLocationId) ||
                             x.Schedule.Routines.Any(r => targetIds.Contains(r.LocationId))))
                .Take(20)
                .ToList();
        }

        // Efficient query for simulation-updated locations using the extended Character_Search index.
        // This replaces the previous unconditional .Take(100) + client-side LINQ filter (O(n) scan).
        var npcsFromSimulation = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereIn("CurrentLocationId", targetIds)
            .Take(20)
            .ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            npcsFromSimulation = npcsFromSimulation.Where(n => string.IsNullOrEmpty(n.CampaignName) || n.CampaignName == effective).ToList();
        }

        // Merge, dedupe by Id, prefer simulation-updated versions when both exist
        var npcMap = npcsFromIndex.ToDictionary(n => n.Id, n => n);
        foreach (var simNpc in npcsFromSimulation)
        {
            npcMap[simNpc.Id] = simNpc; // simulation state wins
        }
        var npcs = npcMap.Values.ToList();

        // Apply campaign scoping filter (loose for shareable NPCs per design)
        if (!string.IsNullOrEmpty(effective))
        {
            npcs = npcs.Where(n => string.IsNullOrEmpty(n.CampaignName) || n.CampaignName == effective).ToList();
        }

        var rumors = await QueryRumorsAsync(session, null, regionId, null, 5, effective);
        
        // Items filtered for campaign scoping (loose for shareables).
        var items = await session.Query<Item>().Where(x => x.HolderId == locationId).ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            items = items.Where(i => string.IsNullOrEmpty(i.CampaignName) || i.CampaignName == effective).ToList();
        }
        foreach (var it in items)
        {
            JsonSanitizer.Sanitize(it);
        }

        JsonSanitizer.Sanitize(location);

        var events = (await QueryEventsAsync(session, null, null, 5, effective))
            .Where(e => e.Involved.Contains(locationId))
            .OrderByDescending(e => e.Timestamp)
            .Take(5)
            .ToList();

        var time = await GetTimeAsync(session, effective);

        // Load global descriptors once (cheap) so we can merge them into every NPC's view
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(session, effective);

        // Project to lightweight presence summaries + behavioral synthesis.
        // This fulfills the V4 goal of giving the DM synthesized insight instead of raw data.
        var presenceSummaries = npcs.Select(npc =>
        {
            var npcNeeds = npc.Needs ?? new NeedsProfile();
            var npcPsych = npc.Psychology ?? new PsychologyProfile();

            // Take the top 3 highest needs for a compact view (sorted descending)
            var topNeeds = npcNeeds.ActiveNeeds
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Expose all known needs + descriptors (merged global + per-NPC, per-NPC wins)
            var knownNeeds = npcNeeds.ActiveNeeds.ToDictionary(kv => kv.Key, kv => kv.Value);
            var needDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcNeeds.NeedDescriptors ?? new Dictionary<string, string>())
            {
                needDescriptors[kv.Key] = kv.Value;
            }

            // Generate behavioral summary using the injected synthesizer
            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, time, events);

            return new NpcPresenceSummary(
                Id: npc.Id,
                Name: npc.Name,
                CurrentActivity: npc.CurrentActivity ?? "Idle at default location",
                CurrentMood: npcPsych.CurrentMood,
                TopNeeds: topNeeds,
                KnownNeeds: knownNeeds,
                NeedDescriptors: needDescriptors,
                BehavioralSummary: behavioralSummary,
                Notes: npc.Notes,
                KeepAlive: npc.KeepAlive,
                CurrentAppearance: npc.CurrentAppearance,
                VisualTags: npc.VisualTags,
                DistinctiveFeatures: npc.DistinctiveFeatures,
                Memories: npcPsych.Memories,
                SystemStats: npc.SystemStats
            );
        }).ToList();

        var activeCombat = await session.LoadAsync<CombatEncounter>(_keys.CombatCurrent(effective));
        if (activeCombat != null && (!activeCombat.IsActive || activeCombat.LocationId != locationId))
        {
            activeCombat = null;
        }

        if (markVisited)
        {
            location.LastVisitedDay = time.TotalDaysElapsed;
        }

        // Phase 7.4: Populate quest + faction context for the location.
        var activeQuests = await GetActiveQuestsForLocationAsync(session, locationId, effective);
        var questSummaries = activeQuests.Select(q => new ActiveQuestSummary(
            q.Id,
            q.Title,
            q.Objectives.Count(o => o.State == QuestState.Open || o.State == QuestState.InProgress),
            q.Objectives.Count,
            q.Urgency,
            q.DeadlineDay,
            q.GiverId,
            q.LastUpdatedDay
        )).ToList();

        var relevantFactions = await GetFactionsForLocationAsync(session, locationId, effective);
        var factionSummaries = relevantFactions.Select(f =>
        {
            int? rep = null;
            var playerRepChar = npcs.FirstOrDefault(c => c.Social?.FactionReputations?.ContainsKey(f.Id) == true);
            if (playerRepChar is { Social.FactionReputations: not null })
            {
                rep = playerRepChar.Social.FactionReputations[f.Id];
            }

            var localStance = FactionStance.Neutral;
            if (f.StanceToward != null)
            {
                foreach (var other in relevantFactions)
                {
                    if (other.Id == f.Id)
                    {
                        continue;
                    }

                    if (f.StanceToward.TryGetValue(other.Id, out var stance))
                    {
                        if (stance == FactionStance.AtWar) { localStance = FactionStance.AtWar; break; }
                        if (stance == FactionStance.Hostile && localStance != FactionStance.AtWar)
                        {
                            localStance = FactionStance.Hostile;
                        }

                        if (stance == FactionStance.Allied && localStance == FactionStance.Neutral)
                        {
                            localStance = FactionStance.Allied;
                        }
                    }
                }

                if (f.StanceToward.TryGetValue("party", out var partyStance) && partyStance == FactionStance.Opportunistic)
                {
                    localStance = FactionStance.Opportunistic;
                }
            }

            return new FactionPresenceSummary(
                f.Id,
                f.Name,
                f.InfluenceLevel,
                localStance,
                rep,
                f.TerritoryLocationIds.Count,
                f.EconomicDemand
            );
        }).ToList();

        var travelEvent = events
            .FirstOrDefault(e => e.Summary.Contains("travel", StringComparison.OrdinalIgnoreCase) ||
                                 e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase) ||
                                 e.Summary.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
        var lastKnownTravel = travelEvent?.Summary;

        var sceneView = new SceneView
        {
            Location = location,
            PresentNPCs = presenceSummaries,
            LocalRumors = rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)),
            VisibleItems = items,
            RecentEvents = events,
            ActiveCombat = activeCombat,
            IsLocationAnchored = true,
            ActiveQuests = questSummaries,
            RelevantFactions = factionSummaries,
            LastKnownTravel = lastKnownTravel,
            SuggestedCommitExamples = new List<string>()
        };
        return sceneView;
    }

    // --- Time & Simulator ---

    /// <summary>
    /// Fast-forwards the campaign time by the specified number of days and runs the background world simulation.
    /// Returns the updated time and narrative events generated by the simulation.
    /// </summary>
    public async Task<AdvanceResult> AdvanceWorldAsync(IAsyncDocumentSession session, int days, TimeOfDay timeOfDay, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var time = await GetTimeAsync(session, effective);

        time.TotalDaysElapsed += days;

        // Recompute Year/Month/Day from TotalDaysElapsed using the fixed 360-day (12×30) fantasy calendar.
        // TotalDaysElapsed is the single source of truth (used by simulation rules, rumor expiry, etc.).
        // This eliminates drift and long loops from the previous Day += + while-loop approach.
        var total = time.TotalDaysElapsed;
        time.Year = 1492 + (total / 360);
        time.Month = ((total % 360) / 30) + 1;
        time.Day = (total % 30) + 1;

        time.TimeOfDay = timeOfDay;

        await session.StoreAsync(time);

        // Scoping hardened: entity queries now filter by CampaignName (see code_review.md and plan).
        // For shareables (NPCs/locs) loose filter allows cross-camp if desired; events/rumors strict.
        // Per user feedback: no BC for play data (none exists), don't support global where doesn't make sense (e.g. no global events).
        var activeRumors = (await session.Query<Rumor>()
            .Where(x => x.State != RumorState.Resolved && x.State != RumorState.Forgotten)
            .ToListAsync())
            .Where(r => string.IsNullOrEmpty(r.CampaignName) || string.Equals(r.CampaignName, effective, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var npcs = (await session.Query<Character>().Where(x => x.Schedule != null).ToListAsync())
            .Where(c => string.IsNullOrEmpty(c.CampaignName) || string.Equals(c.CampaignName, effective, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Phase 7.1: Load active factions and quests so simulation rules can reason about them.
        var activeFactions = (await session.Query<Faction>().Take(200).ToListAsync())
            .Where(f => string.IsNullOrEmpty(f.CampaignName) || string.Equals(f.CampaignName, effective, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var activeQuests = (await session.Query<Quest>()
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(200).ToListAsync())
            .Where(q => string.IsNullOrEmpty(q.CampaignName) || string.Equals(q.CampaignName, effective, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var config = await GetCampaignConfigAsync(session, effective);
        var simContext = new SimulationContext(time, activeRumors, npcs, session, days, effective, activeFactions, activeQuests, config);

        _logger.LogInformation("Starting world simulation for {Days} days at time {CurrentTime}", days, time);

        var simResult = await _simulationEngine.RunAsync(simContext);

        _logger.LogInformation(
            "Simulation complete. Narratives: {NarrativeCount}, Deltas: {DeltaCount}, PressureItems: {PressureCount}",
            simResult.NarrativeEvents.Count,
            simResult.Deltas.Count,
            simResult.WorldPressure.Count);

        // Persist simulation narrative events
        foreach (var narrative in simResult.NarrativeEvents)
        {
            await LogEventAsync(session, new()
            { 
                Id = "events/" + Guid.NewGuid(), 
                Summary = narrative, 
                Category = EventCategory.Simulation,
                DayLogged = time.TotalDaysElapsed 
            }, effective);
        }

        // Apply any deltas produced by simulation rules through the unified Commit path.
        // This gives us clamping, optimistic concurrency, summary logging, etc. for free.
        if (simResult.Deltas.Count > 0)
        {
            _logger.LogDebug("Applying {DeltaCount} simulation deltas", simResult.Deltas.Count);
            await StageChangesAsync(session, simResult.Deltas.ToArray(), effective);
        }

        // WorldPressure from the engine can be surfaced by the caller (AdvanceWorld tool) if desired.
        // For now we keep AdvanceResult focused on time + narratives (matching prior contract).

        return new()
        { 
            NewTime = time, 
            SimulatorEvents = simResult.NarrativeEvents.ToList() 
        };
    }

    // --- Search & Recall ---

    /// <summary>
    /// Performs a parallel, fuzzy search across Characters, Lore, and Locations for the given query.
    /// Returns a mixed collection of documents matching the search string.
    /// </summary>
    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query, string? campaignName = null)
    {
        // Await queries individually. The previous Task-capture + WhenAll + re-await pattern
        // could leave RavenDB session tracking "active async tasks" after the method returned,
        // causing "Disposing session with active async task is forbidden" on ExecuteAsync disposal.
        var chars = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();

        var lore = await session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>()
            .Search(x => x.Title, $"*{query}*").Take(5).ToListAsync();

        var locs = await session.Advanced.AsyncDocumentQuery<Location, Location_Search>()
            .Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();

        // Critical: Locations returned to the LLM via SearchWorld can contain Metadata dictionaries
        // that hold JsonElement (from STJ inbound or legacy data). Without sanitization here,
        // STJ serialization of the tool response in the MCP layer blows up with
        // "Operation is not valid due to the current state of the object" (dead JsonElement).
        foreach (var l in locs)
        {
            SanitizeLocation(l);
        }

        var effective = ResolveCampaign(campaignName);
        if (!string.IsNullOrEmpty(effective))
        {
            chars = chars.Where(c => string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective).ToList();  // loose for chars (may share)
            lore = lore.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective).ToList();
            locs = locs.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective).ToList();
        }

        var results = new List<object>();
        results.AddRange(chars);
        results.AddRange(lore);
        results.AddRange(locs);
        return results;
    }

    /// <summary>
    /// Retrieves historical narrative events, optionally filtered by search query or event category.
    /// </summary>
    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query, EventCategory? category, int limit = 10, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Summary, $"*{query}*");
        }

        if (category.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.Category, category.Value);
        }

        var events = await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
        foreach (var ev in events) { if (ev.Details != null)
            {
                ev.Details = SanitizeDetails(ev.Details);
            }
        }
        if (!string.IsNullOrEmpty(effective))
        {
            // strict for events (no legacy global cross-camp)
            events = events.Where(e => e.CampaignName == effective).ToList();
        }
        return events;
    }

    // --- Base Helpers ---

    /// <summary>
    /// Looks up a character by explicit document ID or performs a fuzzy search on the character's name.
    /// </summary>
    public async Task<Character?> GetCharacterAsync(IAsyncDocumentSession session, string identifier, string? campaignName = null)
    {
        // campaignName accepted for API consistency / future entity namespacing or filtering.
        // Current implementation uses direct ID or name lookup (entities are caller-ID-controlled).
        _ = ResolveCampaign(campaignName);
        var character = await session.LoadAsync<Character>(identifier);
        if (character != null)
        {
            return character;
        }

        character = await session.Query<Character>().FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null)
        {
            return character;
        }

        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>().WhereEquals(x => x.Name, identifier).Fuzzy(0.4m).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Inserts or updates a character in the database, safely mutating tracked entities to preserve concurrency.
    /// Also waits for the Character/Search index to catch up to prevent stale queries.
    /// </summary>
    public async Task UpsertCharacterAsync(IAsyncDocumentSession session, Character character, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
        {
            throw new ArgumentException("Character.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(character.CampaignName))
        {
            character.CampaignName = effective;
        }

        character.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Character>(character.Id);
        if (existing != null)
        {
            // Mutate the already-tracked entity in place. This is the safest pattern
            // with OptimisticConcurrencyMode.Writes + Raven change tracking.
            // We get full overwrite semantics without ever having two objects for the same ID.
            existing.Name = character.Name;
            existing.ClassLevel = character.ClassLevel;
            existing.CurrentHp = character.CurrentHp;
            existing.MaxHp = character.MaxHp;

            existing.Notes = character.Notes;
            existing.Schedule = character.Schedule;
            existing.CurrentLocationId = character.CurrentLocationId;
            existing.CurrentActivity = character.CurrentActivity;
            existing.Psychology = character.Psychology ?? new PsychologyProfile();
            existing.Social = character.Social ?? new SocialProfile();
            existing.Needs = character.Needs ?? new NeedsProfile();
            existing.SystemStats = character.SystemStats ?? new SystemExtension();
            existing.KeepAlive = character.KeepAlive;
            existing.LastUpdated = character.LastUpdated;
            existing.CampaignName = character.CampaignName;  // ensure set/copied for scoping
        }
        else
        {
            await session.StoreAsync(character, null, character.Id);
        }

        // Help keep the Character/Search index fresh after writes that affect Schedule or CurrentLocation.
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: ["Character/Search"]);
    }

    /// <summary>
    /// Retrieves the current time for the specified campaign. Returns a new zeroed time object if none exists.
    /// </summary>
    public async Task<CampaignTime> GetTimeAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateTime(effective);
        var time = await session.LoadAsync<CampaignTime>(id);
        if (time == null) { time = new() { Id = id }; await session.StoreAsync(time, id); }
        return time;
    }

    /// <summary>
    /// Saves the provided time object for the specified campaign, updating its last modified timestamp.
    /// </summary>
    public async Task SaveTimeAsync(IAsyncDocumentSession session, CampaignTime time, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        time.Id = _keys.StateTime(effective);
        time.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(time);
    }

    /// <summary>
    /// Retrieves the configuration for the specified campaign, initializing a new default config if missing.
    /// </summary>
    public async Task<CampaignConfig> GetCampaignConfigAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.Config(effective);
        var config = await session.LoadAsync<CampaignConfig>(id);
        if (config == null)
        {
            config = new() { Id = id };
            await session.StoreAsync(config, id);
        }
        return config;
    }

    /// <summary>
    /// Updates the configuration settings (like the active ruleset) for the specified campaign.
    /// </summary>
    public async Task UpsertCampaignConfigAsync(IAsyncDocumentSession session, CampaignConfig config, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.Config(effective);
        config.Id = id;
        await session.StoreAsync(config, id);
    }

    /// <summary>
    /// Returns globally defined need descriptors (populated via the DefineNeedDescriptor tool).
    /// These act as a shared dictionary that individual NPCs can reference or override via Mind.NeedDescriptors.
    /// </summary>
    public async Task<Dictionary<string, string>> GetGlobalNeedDescriptorsAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId);
        var source = config?.Descriptors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets or updates a single need descriptor for a campaign.
    /// </summary>
    public async Task SetNeedDescriptorAsync(IAsyncDocumentSession session, string needName, string descriptor, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId) ?? new NeedDescriptorsConfig { Id = docId };
        config.Descriptors[needName.Trim()] = descriptor.Trim();
        await session.StoreAsync(config, docId);
    }

    /// <summary>
    /// Logs a narrative event into the campaign's history, securely sanitizing any complex JSON details.
    /// </summary>
    public async Task LogEventAsync(IAsyncDocumentSession session, Event @event, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(@event.CampaignName))
        {
            @event.CampaignName = effective;  // strict for events (campaign-specific per feedback)
        }

        if (@event.Details != null)
        {
            @event.Details = SanitizeDetails(@event.Details);
        }

        await session.StoreAsync(@event);
    }

    // All sanitization logic is now centralized in JsonSanitizer.
    // These methods are thin wrappers for backward compatibility inside the repository
    // and for explicit calls from tools / tests.

    private IDictionary<string, object> SanitizeDetails(IDictionary<string, object> details)
        => (IDictionary<string, object>?)JsonSanitizer.SanitizeDictionary(details) ?? details;

    private Dictionary<string, object> SanitizeDictionary(IDictionary<string, object> source)
        => (Dictionary<string, object>)JsonSanitizer.SanitizeDictionary(source)!;

    private object SanitizeValue(object? value)
        => JsonSanitizer.SanitizeValue(value) ?? value!;

    /// <summary>
    /// Applies JSON sanitization to an Event's Details (prevents JsonElement leakage).
    /// </summary>
    public void SanitizeEvent(Event ev)
    {
        JsonSanitizer.Sanitize(ev);
    }

    /// <summary>
    /// Sanitizes Location.Metadata. Safe to call multiple times.
    /// </summary>
    public void SanitizeLocation(Location? loc)
    {
        JsonSanitizer.Sanitize(loc);
    }

    /// <summary>
    /// Sanitizes Item.Properties. Safe to call multiple times.
    /// </summary>
    public void SanitizeItem(Item? item)
    {
        JsonSanitizer.Sanitize(item);
    }

    /// <summary>
    /// Universal sanitization entry point. Delegates to the central JsonSanitizer.
    /// </summary>
    public void SanitizeEntity(object? entity) => JsonSanitizer.Sanitize(entity);

    /// <summary>
    /// Best-effort deep sanitization of tool response payloads before STJ serialization
    /// in the MCP layer. Delegates to the central JsonSanitizer.
    /// </summary>
    public void SanitizeForToolResponse(object? value) => JsonSanitizer.SanitizeForToolResponse(value);

    /// <summary>
    /// Creates or updates a piece of Lore, handling creation/update timestamps.
    /// </summary>
    public async Task UpsertLoreAsync(IAsyncDocumentSession session, Lore lore, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(lore.Id))
        {
            throw new ArgumentException("Lore.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(lore.CampaignName))
        {
            lore.CampaignName = effective;
        }

        lore.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Lore>(lore.Id);
        if (existing != null)
        {
            existing.Title = lore.Title;
            existing.Content = lore.Content;
            existing.Tags = lore.Tags ?? [];
            existing.Keywords = lore.Keywords ?? [];
            existing.Category = lore.Category;
            existing.LastUpdated = lore.LastUpdated;
            existing.CampaignName = lore.CampaignName;  // ensure set/copied for scoping
        }
        else
        {
            await session.StoreAsync(lore);
        }
    }

    /// <summary>
    /// Searches for Lore entries by fuzzy title/content match, or strictly by tags and category.
    /// </summary>
    public async Task<IEnumerable<Lore>> QueryLoreAsync(IAsyncDocumentSession session, string? query, string[]? tags, string? category, int limit = 5, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.OpenSubclause().WhereEquals(x => x.Title, query).Fuzzy(0.4m).OrElse().WhereEquals(x => x.Content, query).Fuzzy(0.4m).CloseSubclause();
        }

        if (tags is { Length: > 0 }) { foreach (var tag in tags)
            {
                q = q.AndAlso().ContainsAny(x => x.Tags, [tag]);
            }
        }
        if (!string.IsNullOrEmpty(category))
        {
            q = q.AndAlso().WhereEquals(x => x.Category, category);
        }

        var list = await q.Take(limit).ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            list = list.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective).ToList();  // loose for lore (may share)
        }
        return list;
    }

    /// <summary>
    /// Creates or updates a Location, handling sanitization of arbitrary metadata.
    /// </summary>
    public async Task UpsertLocationAsync(IAsyncDocumentSession session, Location location, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(location.Id))
        {
            throw new ArgumentException("Location.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(location.CampaignName))
        {
            location.CampaignName = effective;
        }

        SanitizeLocation(location);
        location.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Location>(location.Id);
        if (existing != null)
        {
            // Mutate the tracked entity (safest with optimistic concurrency).
            existing.Name = location.Name;
            existing.Description = location.Description;
            existing.Type = location.Type;
            existing.ParentLocationId = location.ParentLocationId;
            existing.Exits = location.Exits ?? [];
            existing.PointsOfInterest = location.PointsOfInterest ?? [];
            existing.AmbientCrowd = location.AmbientCrowd;
            existing.LastVisitedDay = location.LastVisitedDay;
            existing.Metadata = location.Metadata ?? [];
            existing.LastUpdated = location.LastUpdated;
            existing.CampaignName = location.CampaignName;  // ensure set/copied for scoping
            existing.ControllingFactionId = location.ControllingFactionId;  // Phase 7.1
        }
        else
        {
            await session.StoreAsync(location);
        }
    }

    /// <summary>
    /// Queries Locations by fuzzy name/description, or strictly by type and parent region.
    /// </summary>
    public async Task<IEnumerable<Location>> QueryLocationsAsync(IAsyncDocumentSession session, string? query, LocationType? type = null, string? parentId = null, int limit = 10, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Location, Location_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Name, $"*{query}*").OrElse().Search(x => x.Description, $"*{query}*");
        }

        if (type.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.Type, type.Value);
        }

        if (!string.IsNullOrEmpty(parentId))
        {
            q = q.AndAlso().WhereEquals(x => x.ParentLocationId, parentId);
        }

        var locations = await q.Take(limit).ToListAsync();
        foreach (var l in locations)
        {
            SanitizeLocation(l);
        }
        if (!string.IsNullOrEmpty(effective))
        {
            locations = locations.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective).ToList();  // loose for locations (may share)
        }

        return locations;
    }

    /// <summary>
    /// Inserts or updates a Rumor, initializing its creation day to the current campaign time if omitted.
    /// </summary>
    public async Task UpsertRumorAsync(IAsyncDocumentSession session, Rumor rumor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(rumor.Id))
        {
            throw new ArgumentException("Rumor.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(rumor.CampaignName))
        {
            rumor.CampaignName = effective;  // strict for rumors (campaign-specific per feedback)
        }

        rumor.LastUpdated = DateTime.UtcNow;
        if (rumor.DayCreated == 0)
        {
            var t = await GetTimeAsync(session, effective);
            rumor.DayCreated = t.TotalDaysElapsed;
            rumor.LastStateChangeDay = t.TotalDaysElapsed;
        }

        var existing = await session.LoadAsync<Rumor>(rumor.Id);
        if (existing != null)
        {
            existing.Subject = rumor.Subject;
            existing.CurrentText = rumor.CurrentText;
            existing.State = rumor.State;
            existing.RegionLocationId = rumor.RegionLocationId;
            existing.DayCreated = rumor.DayCreated;
            existing.LastStateChangeDay = rumor.LastStateChangeDay;
            existing.LastUpdated = rumor.LastUpdated;
            existing.CampaignName = rumor.CampaignName;  // ensure for scoping (strict for rumors)
        }
        else
        {
            await session.StoreAsync(rumor);
        }
    }

    /// <summary>
    /// Queries active Rumors by fuzzy subject/text, or strictly by region and state.
    /// </summary>
    public async Task<IEnumerable<Rumor>> QueryRumorsAsync(IAsyncDocumentSession session, string? query, string? regionId = null, RumorState? state = null, int limit = 5, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Rumor, Rumor_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Subject, $"*{query}*").OrElse().Search(x => x.CurrentText, $"*{query}*");
        }

        if (!string.IsNullOrEmpty(regionId))
        {
            q = q.AndAlso().WhereEquals(x => x.RegionLocationId, regionId);
        }

        if (state.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.State, state.Value);
        }

        var list = await q.Take(limit).ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            // strict for rumors (no legacy global cross-camp)
            list = list.Where(r => r.CampaignName == effective).ToList();
        }
        return list;
    }

    /// <summary>
    /// Retrieves a specific Location by ID, sanitizing any metadata upon read.
    /// </summary>
    public async Task<Location?> GetLocationAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var loc = await session.LoadAsync<Location>(id);
        SanitizeLocation(loc);
        return loc;
    }

    /// <summary>
    /// Retrieves a specific Item by ID.
    /// </summary>
    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        // campaignName accepted for API consistency / future entity namespacing.
        _ = ResolveCampaign(campaignName);
        return await session.LoadAsync<Item>(id);
    }

    /// <summary>
    /// Inserts or updates an Item, sanitizing arbitrary properties and preserving optimistic concurrency on edits.
    /// </summary>
    public async Task UpsertItemAsync(IAsyncDocumentSession session, Item item, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("Item.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(item.CampaignName))
        {
            item.CampaignName = effective;
        }

        SanitizeItem(item);
        item.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Item>(item.Id);
        if (existing != null)
        {
            existing.Name = item.Name;
            existing.Description = item.Description;
            existing.Properties = item.Properties ?? [];
            existing.HolderId = item.HolderId;
            existing.LastUpdated = item.LastUpdated;
            existing.CampaignName = item.CampaignName;  // ensure set/copied for scoping
            // Note: original missed copying Tags; added for completeness in hardening pass
            existing.Tags = item.Tags ?? existing.Tags ?? [];
        }
        else
        {
            await session.StoreAsync(item);
        }
    }

    public async Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("locations/".Length);
        }
        else if (cleanQuery.StartsWith("locs/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("locs/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var suggestions = await session.Query<Location, Location_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Location, Location_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var item in byName)
            {
                if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(item);
                }
            }
        }
        return suggestions;
    }

    public async Task<List<Character>> SuggestCharactersAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("chars/".Length);
        }
        else if (cleanQuery.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("characters/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var suggestions = await session.Query<Character, Character_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var item in byName)
            {
                if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(item);
                }
            }
        }
        return suggestions;
    }

    public async Task<List<Item>> SuggestItemsAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("items/".Length);
        }
        else if (cleanQuery.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("item/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var suggestions = await session.Query<Item, Item_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Item, Item_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var item in byName)
            {
                if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(item);
                }
            }
        }
        return suggestions;
    }

    /// <summary>
    /// Suggests Factions by fuzzy name match or ID prefix. Used in error messages and views.
    /// </summary>
    public async Task<List<Faction>> SuggestFactionsAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("factions/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var suggestions = await session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var f in byName)
            {
                if (suggestions.All(s => s.Id != f.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(f);
                }
            }
        }
        return suggestions;
    }

    /// <summary>
    /// Suggests Quests by fuzzy name match or ID prefix. Used in error messages for get_quest_details and views.
    /// </summary>
    public async Task<List<Quest>> SuggestQuestsAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("quests/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var suggestions = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == campaignName || x.CampaignName == null)
                .Search(x => x.Title, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var q in byName)
            {
                if (suggestions.All(s => s.Id != q.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(q);
                }
            }
        }
        return suggestions;
    }

    public async Task<List<Quest>> GetActiveQuestsAsync(IAsyncDocumentSession session, string? campaignName = null, int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var quests = await session.Query<Quest, Quest_Search>()
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(limit).ToListAsync();
        return quests.Where(q => string.IsNullOrEmpty(q.CampaignName) || q.CampaignName == effective).ToList();
    }

    public async Task<List<Faction>> GetActiveFactionsAsync(IAsyncDocumentSession session, string? campaignName = null, int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var factions = await session.Query<Faction, Faction_Search>().Take(limit).ToListAsync();
        return factions.Where(f => string.IsNullOrEmpty(f.CampaignName) || f.CampaignName == effective).ToList();
    }

    /// <summary>
    /// Retrieves a specific Faction by ID.
    /// </summary>
    public async Task<Faction?> GetFactionAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        _ = ResolveCampaign(campaignName);
        return await session.LoadAsync<Faction>(id);
    }

    /// <summary>
    /// Creates or updates a Faction document.
    /// </summary>
    public async Task UpsertFactionAsync(IAsyncDocumentSession session, Faction faction, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(faction.CampaignName))
        {
            faction.CampaignName = effective;
        }

        faction.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Faction>(faction.Id);
        if (existing != null)
        {
            existing.Name = faction.Name;
            existing.Description = faction.Description;
            existing.FactionType = faction.FactionType;
            existing.ControllingTerritory = faction.ControllingTerritory;
            existing.TerritoryLocationIds = faction.TerritoryLocationIds ?? [];
            existing.KnownLeaderIds = faction.KnownLeaderIds ?? [];
            existing.InfluenceLevel = faction.InfluenceLevel;
            existing.StanceToward = faction.StanceToward ?? [];
            existing.Metadata = faction.Metadata ?? [];
            existing.LastUpdated = faction.LastUpdated;
            existing.CampaignName = faction.CampaignName;
        }
        else
        {
            await session.StoreAsync(faction);
        }
    }

    /// <summary>
    /// Retrieves a specific Quest by ID.
    /// </summary>
    public async Task<Quest?> GetQuestAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        _ = ResolveCampaign(campaignName);
        return await session.LoadAsync<Quest>(id);
    }

    /// <summary>
    /// Creates or updates a Quest document.
    /// </summary>
    public async Task UpsertQuestAsync(IAsyncDocumentSession session, Quest quest, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(quest.CampaignName))
        {
            quest.CampaignName = effective;
        }

        quest.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Quest>(quest.Id);
        if (existing != null)
        {
            existing.Title = quest.Title;
            existing.GiverId = quest.GiverId;
            existing.Objectives = quest.Objectives ?? [];
            existing.OverallState = quest.OverallState;
            existing.Category = quest.Category;
            existing.Urgency = quest.Urgency;
            existing.RelatedLocationIds = quest.RelatedLocationIds ?? [];
            existing.RelatedFactionIds = quest.RelatedFactionIds ?? [];
            existing.DmNotes = quest.DmNotes;
            existing.VisibleToCharacterIds = quest.VisibleToCharacterIds;
            existing.LastUpdatedDay = quest.LastUpdatedDay;
            existing.LastUpdated = quest.LastUpdated;
            existing.CampaignName = quest.CampaignName;
        }
        else
        {
            await session.StoreAsync(quest);
        }
    }

    /// <summary>
    /// Queries active quests relevant to a specific location (RelatedLocationIds overlap).
    /// Used by GetScene to surface quest summaries.
    /// </summary>
    public async Task<List<Quest>> GetActiveQuestsForLocationAsync(IAsyncDocumentSession session, string locationId, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var quests = await session.Query<Quest, Quest_Search>()
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(20).ToListAsync();

        return quests
            .Where(q => (string.IsNullOrEmpty(q.CampaignName) || q.CampaignName == effective)
                        && q.RelatedLocationIds.Contains(locationId))
            .ToList();
    }

    /// <summary>
    /// Queries active factions that have territory overlapping with a given location ID.
    /// Used by GetScene to surface relevant faction context.
    /// </summary>
    public async Task<List<Faction>> GetFactionsForLocationAsync(IAsyncDocumentSession session, string locationId, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var factions = await session.Query<Faction, Faction_Search>()
            .Take(50).ToListAsync();

        return factions
            .Where(f => (string.IsNullOrEmpty(f.CampaignName) || f.CampaignName == effective)
                        && (f.ControllingTerritory == locationId || f.TerritoryLocationIds.Contains(locationId)))
            .ToList();
    }
}

