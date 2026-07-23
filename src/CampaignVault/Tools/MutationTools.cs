using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase, IMcpServerTool
{
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;

    // Keyed per-campaign so commits in one campaign never throttle another.
    private static readonly ConcurrentDictionary<string, RateLimiter> CommitRateLimiters = new(StringComparer.OrdinalIgnoreCase);

    private static RateLimiter GetRateLimiter(string campaignName) =>
        CommitRateLimiters.GetOrAdd(campaignName, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 50,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true
        }));

    public MutationTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator,
        ILogger<MutationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIFIED TURN TOOL: Call this at the end of any narrative beat (combat, conversation, discovery) for atomic mutations + bundled fresh state in one round-trip.

REPLACES the old pattern: get_scene → commit → get_scene again. Instead: one take_turn call with optional mutations and refresh params, getting back the commit outcome + fresh entity summaries in one response.

AUTO-REFRESH enabled by default (autoRefreshInvolved: true): the response includes lightweight summaries of any entities touched by the commit, capped at 6 NPCs and 3 scenes. Opt out with autoRefreshInvolved: false for bulk/seeding commits.

Pure queries (no Changes): pass just the refresh params with Changes omitted to refresh specific entities without mutations.")]
    public Task<ToolResult<TurnResult>> TakeTurn(
        [Description("Bundled turn request: optional mutations (Changes+Narrative) and/or entity refresh requests (AutoRefreshInvolved, ExtraCharacterIds, ExtraLocationIds).")]
        TakeTurnRequest request,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        if (request?.Changes is not null && request.Changes.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(request.Narrative))
            {
                return ToolArgumentErrors.Missing<TurnResult>(
                    "narrative",
                    "Provide a short summary of what happened for the event log when Changes are provided.",
                    toolName: "take_turn");
            }

            if (request.Changes.Length > 50)
            {
                return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                    Summary: $"Commit rejected: Too many changes in a single batch ({request.Changes.Length}). Maximum is 50."));
            }

            var duplicationConflict = SideEffectDuplicationGuard.FindConflict(request.Changes);
            if (duplicationConflict != null)
            {
                return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.InvalidArgument,
                    Summary: $"Commit rejected: {duplicationConflict}"));
            }
        }

        if (!TryGetEffectiveCampaign(campaignName, out var effective))
        {
            return Task.FromResult(new ToolResult<TurnResult>(
                false,
                Error: ToolErrors.NoCampaignSelected,
                Summary: NoCampaignSelectedSummary));
        }

        var rateLimiter = GetRateLimiter(effective);

        if (request?.Changes is not null && request.Changes.Length > 0)
        {
            if (!rateLimiter.AttemptAcquire().IsAcquired)
            {
                return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                    Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
            }
        }

        return ExecuteAsync(async session =>
        {
            var result = new TurnResult();

            if (request?.Changes is not null && request.Changes.Length > 0)
            {
                var commitResult = await _repository.StageChangesAsync(session, request.Changes, effective);
                if (!commitResult.Success)
                {
                    var errorMsg = "NO CHANGES WERE SAVED — the entire batch was rolled back because at least one " +
                                   "change failed validation. Fix the error(s) below and resend the FULL batch " +
                                   "(not just the failed item).\n" + string.Join("\n", commitResult.Summary);
                    return new ToolResult<TurnResult>(false, new TurnResult(), Summary: errorMsg, Error: "ValidationError");
                }

                result.Committed = true;
                result.ChangesProcessed = commitResult.ChangesProcessed;
                result.Summary = commitResult.Summary;
                result.InvolvedEntities = commitResult.InvolvedEntities;
                result.EntityCollisions = commitResult.EntityCollisions;
                result.NarrativeReminder = commitResult.NarrativeReminder;

                var commitTime = await _repository.GetTimeAsync(session, effective);
                await _repository.LogEventAsync(session,
                    new Event
                    {
                        Id = "events/" + Guid.NewGuid(),
                        CampaignName = effective,
                        Summary = request.Narrative!,
                        Category = EventCategory.SceneCommit,
                        Involved = commitResult.InvolvedEntities,
                        DayLogged = (int)commitTime.TotalDaysElapsed
                    },
                    effective);

                var hasCombatMutation = request.Changes.Any(c => c is HpChange or RulesetAction or StatusChange);
                var hasNarrativeEvent = request.Changes.Any(c => c is EventOccurred);
                if (hasCombatMutation && !hasNarrativeEvent)
                {
                    result.NarrativeReminder =
                        "This commit included combat/status changes but no 'event' ($type: event). " +
                        "Add an EventOccurred to record the narrative beat.";
                }

                var significantEventLocations = request.Changes.OfType<EventOccurred>()
                    .Where(e => e.Importance is MemoryImportance.Important or MemoryImportance.Core)
                    .SelectMany(e => (e.RelatedLocationIds ?? []).Append(e.LocationId))
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet();
                if (significantEventLocations.Count > 0)
                {
                    var poiCoveredLocations = request.Changes.OfType<LocationUpdate>()
                        .Where(lu => !string.IsNullOrWhiteSpace(lu.MaterializePointOfInterest))
                        .Select(lu => lu.LocationId)
                        .ToHashSet();
                    var uncoveredMoves = request.Changes.OfType<ActivityChange>()
                        .Where(a => a.UpdateLocation && !string.IsNullOrEmpty(a.NewLocationId)
                                    && string.IsNullOrWhiteSpace(a.PoiName)
                                    && significantEventLocations.Contains(a.NewLocationId!)
                                    && !poiCoveredLocations.Contains(a.NewLocationId!))
                        .Select(a => a.NewLocationId!)
                        .Distinct()
                        .ToList();
                    if (uncoveredMoves.Count > 0)
                    {
                        var poiReminder =
                            $"This commit moved a character to {string.Join(", ", uncoveredMoves)} alongside an Important/Core event " +
                            "but recorded no location detail. If the spot matters, add poiName/poiDetails.";
                        result.NarrativeReminder = result.NarrativeReminder is null
                            ? poiReminder
                            : result.NarrativeReminder + " " + poiReminder;
                    }
                }

                await session.SaveChangesAsync();
            }

            if (request?.AutoRefreshInvolved != false && result.InvolvedEntities.Count > 0)
            {
                var toRefresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in result.InvolvedEntities)
                {
                    if (id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith(CanonicalId.Locations, StringComparison.OrdinalIgnoreCase))
                    {
                        toRefresh.Add(id);
                    }
                }

                if (request?.ExtraCharacterIds != null)
                {
                    foreach (var id in request.ExtraCharacterIds)
                    {
                        toRefresh.Add(id);
                    }
                }

                if (request?.ExtraLocationIds != null)
                {
                    foreach (var id in request.ExtraLocationIds)
                    {
                        toRefresh.Add(id);
                    }
                }

                const int NpcCap = 6;
                const int SceneCap = 3;

                var npcsToFetch = new List<string>();
                var scenesToFetch = new List<string>();
                var truncatedIds = new List<string>();

                foreach (var id in toRefresh)
                {
                    if (id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase))
                    {
                        if (npcsToFetch.Count < NpcCap)
                            npcsToFetch.Add(id);
                        else
                            truncatedIds.Add(id);
                    }
                    else if (id.StartsWith(CanonicalId.Locations, StringComparison.OrdinalIgnoreCase))
                    {
                        if (scenesToFetch.Count < SceneCap)
                            scenesToFetch.Add(id);
                        else
                            truncatedIds.Add(id);
                    }
                }

                if (scenesToFetch.Count > 0)
                {
                    result.Scenes = [];
                    foreach (var locationId in scenesToFetch)
                    {
                        try
                        {
                            var scene = await _repository.GetSceneAsync(session, locationId, effective, markVisited: false);
                            if (scene?.Location != null)
                            {
                                var summary = new SceneSummaryView
                                {
                                    Location = scene.Location,
                                    PresentNPCs = scene.PresentNPCs ?? [],
                                    LocalRumors = scene.LocalRumors ?? [],
                                    ActiveCombat = scene.ActiveCombat != null
                                };
                                result.Scenes.Add(summary);
                            }
                        }
                        catch { }
                    }
                }

                if (npcsToFetch.Count > 0)
                {
                    result.Npcs = [];
                    foreach (var charId in npcsToFetch)
                    {
                        try
                        {
                            var npc = await _repository.GetCharacterAsync(session, charId, effective);
                            if (npc != null)
                            {
                                var heldItems = await session.Query<Item>()
                                    .Where(i => i.HolderId == charId && !i.IsArchived)
                                    .Customize(x => x.WaitForNonStaleResults())
                                    .ToListAsync();

                                var summary = new NpcSummaryView
                                {
                                    CharacterId = npc.Id,
                                    Name = npc.Name,
                                    CurrentAppearance = npc.CurrentAppearance ?? "",
                                    BehavioralSummary = "",
                                    KnownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>(),
                                    Equipped = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList(),
                                    Carried = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList()
                                };
                                result.Npcs.Add(summary);
                            }
                        }
                        catch { }
                    }
                }

                if (truncatedIds.Count > 0)
                {
                    result.RefreshTruncatedIds = truncatedIds;
                }
            }

            var stats = rateLimiter.GetStatistics();
            if (stats != null)
            {
                result.RateLimitTokensRemaining = (int)stats.CurrentAvailablePermits;
            }

            var successMsg = result.Committed
                ? $"World updated with {result.ChangesProcessed} changes and fresh state echoed."
                : "State refreshed.";

            return new ToolResult<TurnResult>(true, result, successMsg);
        }, saveChanges: false);
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world.
Accepts a batch of changes (HP, Items — including persistent damage/wear/hidden-feature details via item_update's upsertItemDetail — Events, Rumors, Relationships, Needs, Attributes, Activity, Status, ruleset_action, and world updates).

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, copy-paste examples, and change-type reference.

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided.**")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Batch of world changes and narrative summary.")]
        CommitRequest request,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        if (request?.Changes is null || request.Changes.Length == 0)
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        if (string.IsNullOrWhiteSpace(request.Narrative))
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "narrative",
                "Provide a short summary of what happened for the event log.",
                toolName: "commit");
        }

        return Commit(request.Changes, request.Narrative, campaignName);
    }

    public Task<ToolResult<CommitResult>> Commit(
        WorldChange[]? changes,
        string? narrative = null,
        string? campaignName = null)
    {
        if (changes is null || changes.Length == 0)
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        if (string.IsNullOrWhiteSpace(narrative))
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "narrative",
                "Provide a short summary of what happened for the event log.",
                toolName: "commit");
        }

        if (!TryGetEffectiveCampaign(campaignName, out var effective))
        {
            return Task.FromResult(new ToolResult<CommitResult>(
                false,
                Error: ToolErrors.NoCampaignSelected,
                Summary: NoCampaignSelectedSummary));
        }

        if (changes.Length > 50)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary:
                $"Commit rejected: Too many changes in a single batch ({changes.Length}). Maximum allowed is 50."));
        }

        var duplicationConflict = SideEffectDuplicationGuard.FindConflict(changes);
        if (duplicationConflict != null)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.InvalidArgument,
                Summary: $"Commit rejected: {duplicationConflict}"));
        }

        var rateLimiter = GetRateLimiter(effective);
        if (!rateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        return ExecuteAsync(async session =>
        {
            var result = await _repository.StageChangesAsync(session, changes, effective);
            if (!result.Success)
            {
                var errorMsg = "NO CHANGES WERE SAVED — the entire batch was rolled back because at least one " +
                                "change failed validation. Fix the error(s) below and resend the FULL batch " +
                                "(not just the failed item).\n" + string.Join("\n", result.Summary);
                return new ToolResult<CommitResult>(false, result, Summary: errorMsg,
                    Error: "ValidationError");
            }

            var commitTime = await _repository.GetTimeAsync(session, effective);
            await _repository.LogEventAsync(session,
                new Event
                {
                    Id = "events/" + Guid.NewGuid(), CampaignName = effective, Summary = narrative,
                    Category = EventCategory.SceneCommit, Involved = result.InvolvedEntities,
                    DayLogged = (int)commitTime.TotalDaysElapsed
                }, effective);

            // Warn if the batch contained combat/status mutations but no narrative event
            var hasCombatMutation = changes.Any(c => c is HpChange or RulesetAction or StatusChange);
            var hasNarrativeEvent = changes.Any(c => c is EventOccurred);
            if (hasCombatMutation && !hasNarrativeEvent)
            {
                result.NarrativeReminder =
                    "This commit included combat/status changes but no 'event' ($type: event). " +
                    "Add an EventOccurred to record the narrative beat for future get_npc_context and recall_history queries.";
            }

            // Warn if an activity moved a character somewhere narratively significant with no PoI detail recorded
            var significantEventLocations = changes.OfType<EventOccurred>()
                .Where(e => e.Importance is MemoryImportance.Important or MemoryImportance.Core)
                .SelectMany(e => (e.RelatedLocationIds ?? []).Append(e.LocationId))
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();
            if (significantEventLocations.Count > 0)
            {
                var poiCoveredLocations = changes.OfType<LocationUpdate>()
                    .Where(lu => !string.IsNullOrWhiteSpace(lu.MaterializePointOfInterest))
                    .Select(lu => lu.LocationId)
                    .ToHashSet();
                var uncoveredMoves = changes.OfType<ActivityChange>()
                    .Where(a => a.UpdateLocation && !string.IsNullOrEmpty(a.NewLocationId)
                                && string.IsNullOrWhiteSpace(a.PoiName)
                                && significantEventLocations.Contains(a.NewLocationId!)
                                && !poiCoveredLocations.Contains(a.NewLocationId!))
                    .Select(a => a.NewLocationId!)
                    .Distinct()
                    .ToList();
                if (uncoveredMoves.Count > 0)
                {
                    var poiReminder =
                        $"This commit moved a character to {string.Join(", ", uncoveredMoves)} alongside an Important/Core " +
                        "event referencing that location, but recorded no location detail there. If the narrated spot " +
                        "(cover, hazards, what's hidden) will matter later, add poiName/poiDetails to the activity change " +
                        "(or a paired location_update with materializePointOfInterest) — see get_help topic=patterns, " +
                        "'Ad-Hoc Waypoint Detail'.";
                    result.NarrativeReminder = result.NarrativeReminder is null
                        ? poiReminder
                        : result.NarrativeReminder + " " + poiReminder;
                }
            }

            // Surface remaining rate-limit budget so the LLM can pace large scenes
            var stats = rateLimiter.GetStatistics();
            if (stats != null)
                result.RateLimitTokensRemaining = (int)stats.CurrentAvailablePermits;

            var msg = $"World updated with {changes.Length} changes. Full result in structuredContent.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
    }



    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "TIME PASSAGE FOR SAFE/UNEVENTFUL DOWNTIME: Fast-forwards the world clock and runs simulation rules (needs, " +
        "rumor decay, faction/plot evolution, transient GC) — for a multi-day skip (training montage, downtime between " +
        "arcs, a journey already narrated as uneventful) use days+timeOfDay; for an overnight rest or partial-day span " +
        "use hours instead (e.g. hours:8) and the engine derives the resulting day/timeOfDay for you — no manual day " +
        "math needed. NOTE: this tool has NO encounter/interruption mechanic of its own. If the span carries ANY real " +
        "risk (resting somewhere unsafe, a dangerous overnight, an unescorted journey), commit a 'rest' or 'travel' " +
        "change instead — those roll for interruptions; this tool silently assumes nothing happens. Requires campaignName.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Summary of the rest, travel, or downtime activity.")]
        string narrative,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Number of whole days to skip for a multi-day time jump. Omit when using 'hours' instead — set one or the other, not both.")]
        int days = 0,
        [Description("Resulting time-of-day bucket (Dawn/Morning/Noon/Afternoon/Evening/Dusk/Night). Required when using 'days'. Omit when using 'hours' — derived automatically.")]
        TimeOfDay? timeOfDay = null,
        [Description("Alternative to days/timeOfDay: hours to fast-forward from the CURRENT time (e.g. 8 for sleeping through the night, 4 for a half-day trek). The engine computes the resulting day/timeOfDay for you. Mutually exclusive with days/timeOfDay.")]
        int? hours = null)
    {
        if (hours.HasValue)
        {
            if (hours.Value <= 0)
            {
                return ToolArgumentErrors.Missing<AdvanceResult>(
                    "hours",
                    "hours must be a positive number of hours to fast-forward. Use days+timeOfDay for a multi-day skip instead.",
                    toolName: "advance_world");
            }

            if (days != 0 || timeOfDay.HasValue)
            {
                return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "InvalidArgument",
                    Summary: "Pass either 'hours' OR 'days'+'timeOfDay', not both."));
            }
        }
        else if (days <= 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest",
                Summary: "Cannot advance zero or a negative number of days. Use 'hours' instead for a sub-day/overnight span."));
        }
        else if (!timeOfDay.HasValue)
        {
            return ToolArgumentErrors.Missing<AdvanceResult>(
                "timeOfDay",
                "Required when using 'days' for a multi-day skip. Use 'hours' instead for a same-night/partial-day span, which derives timeOfDay automatically.",
                toolName: "advance_world");
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var result = await _repository.AdvanceWorldAsync(session, days, timeOfDay, effective, hours);

            var partyIds = await session.Query<Character, Character_Search>()
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .Customize(x => x.WaitForNonStaleResults())
                .Select(c => c.Id)
                .ToListAsync();

            await _repository.LogEventAsync(session,
                new Event
                {
                    Id = "events/" + Guid.NewGuid(),
                    CampaignName = effective,
                    Summary = narrative,
                    Category = EventCategory.Timeskip,
                    DayLogged = (int)result.NewTime.TotalDaysElapsed,
                    Involved = partyIds
                },
                effective);

            var timeDoc = result.NewTime;
            var config = await _repository.GetCampaignConfigAsync(session, effective);

            var orchestratorPressures = await _pressureOrchestrator.CollectAndCapAsync(
                PressureScope.World,
                new PressureContext(
                    effective,
                    timeDoc,
                    config,
                    session,
                    DaysAdvanced: result.DaysAdvanced,
                    DisableCooldowns: true));

            // Cooldowns are disabled on this path (DisableCooldowns: true above and
            // disableCooldowns: true below), so content-signature dedupe is the only mechanism that
            // keeps duplicate-text simulator events from flooding a single advance_world response.
            var dedupedSimulatorEvents = result.SimulatorEvents
                .GroupBy(e => PressureHelpers.ComputeContentSignature(e))
                .Select(g => g.First())
                .ToList();

            var rawPressures = dedupedSimulatorEvents
                .Select(e => new WorldPressureItem(PressureSeverity.Simulation, "Simulation", e,
                    WorldPressureItem.SimulationEventGroupingKey))
                .Concat(result.WorldPressure)
                .Concat(orchestratorPressures)
                .ToList();

            List<WorldPressureItem> allPressureItems = [];
            if (rawPressures.Count > 0)
            {
                allPressureItems = await _pressureManager.FilterAndCapAsync(session, effective,
                    (int)timeDoc.TotalDaysElapsed, rawPressures, disableCooldowns: true);
            }

            var cappedPressure = allPressureItems.Count > 0 ? PressureManager.ToDisplayStrings(allPressureItems) : null;

            // Ensure AdvanceResult carries the rich items
            result.WorldPressure = allPressureItems;

            var advancedText = hours.HasValue
                ? $"Advanced {hours} hour(s) ({result.DaysAdvanced} calendar day(s) crossed)."
                : $"Advanced {days} day(s).";

            return new ToolResult<AdvanceResult>(true, result,
                $"{advancedText} {result.SimulatorEvents.Count} events and {allPressureItems.Count} structured pressures generated.",
                WorldPressure: cappedPressure);
        });
    }
}