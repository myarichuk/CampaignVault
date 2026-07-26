using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase, IMcpServerTool
{
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;

    // Keyed per-campaign so commits in one campaign never throttle another. Bounded so a
    // long-running multi-campaign server can't grow this dictionary without limit: past the cap,
    // idle limiters (full token bucket = no recent commits) are evicted and disposed.
    private const int RateLimiterCap = 256;
    private static readonly ConcurrentDictionary<string, RateLimiter> CommitRateLimiters = new(StringComparer.OrdinalIgnoreCase);

    private static RateLimiter GetRateLimiter(string campaignName)
    {
        if (CommitRateLimiters.Count > RateLimiterCap)
        {
            foreach (var (key, limiter) in CommitRateLimiters)
            {
                if (key.Equals(campaignName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (limiter.GetStatistics() is { CurrentAvailablePermits: >= 50 } &&
                    CommitRateLimiters.TryRemove(key, out var removed))
                {
                    removed.Dispose();
                }
            }
        }

        return CommitRateLimiters.GetOrAdd(campaignName, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 50,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true
        }));
    }

    public MutationTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        ILogger<MutationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
        _behaviorSynthesizer = behaviorSynthesizer;
    }

    /// <summary>
    /// Mutable state threaded through the take_turn pipeline steps. Each step reads the request,
    /// enriches <see cref="Result"/>, and records non-fatal problems via <see cref="MutationTools.Warn"/>.
    /// </summary>
    private sealed class TurnContext(TakeTurnRequest? request, string campaign, IAsyncDocumentSession session)
    {
        public TakeTurnRequest? Request { get; } = request;
        public string Campaign { get; } = campaign;
        public IAsyncDocumentSession Session { get; } = session;
        public TurnResult Result { get; } = new();
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIFIED TURN TOOL: Call this at the end of any narrative beat (combat, conversation, discovery) for atomic mutations + bundled fresh state in one round-trip.

One take_turn call carries optional mutations (Changes+Narrative) and optional refresh params, and returns the commit outcome + fresh entity summaries in one response — no separate query-before/query-after calls needed.

AUTO-REFRESH enabled by default (autoRefreshInvolved: true): the response includes lightweight summaries of any entities touched by the commit, capped at 6 NPCs and 3 scenes (explicitly requested extraCharacterIds/extraLocationIds are always served first). Opt out with autoRefreshInvolved: false for bulk/seeding commits.

Pure queries (no Changes): pass just the refresh params with Changes omitted to refresh specific entities without mutations. Check the 'warnings' array in the response for any section that could not be assembled.")]
    public Task<ToolResult<TurnResult>> TakeTurn(
        [Description("Bundled turn request: optional mutations (Changes+Narrative) and/or entity refresh requests (AutoRefreshInvolved, ExtraCharacterIds, ExtraLocationIds).")]
        TakeTurnRequest request,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        var hasChanges = request?.Changes is { Length: > 0 };

        // Validate that this isn't an empty call with no purpose
        if (!hasChanges && request != null)
        {
            var hasRefreshParams = request.IncludeWorldState || request.IncludeParty ||
                                   (request.ExtraCharacterIds?.Length > 0) ||
                                   (request.ExtraLocationIds?.Length > 0) ||
                                   !string.IsNullOrEmpty(request.FullDetailCharacterId) ||
                                   !string.IsNullOrEmpty(request.FullDetailLocationId);

            if (!hasRefreshParams)
            {
                return Task.FromResult(new ToolResult<TurnResult>(
                    false,
                    Error: ToolErrors.InvalidArgument,
                    Summary: "This take_turn call has no Changes and no refresh parameters (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, fullDetailLocationId). Did you mean to commit world changes? Pass at least one refresh param if this is a pure-query call."));
            }
        }

        if (hasChanges)
        {
            var precheckFailure = ValidateChanges(request!);
            if (precheckFailure != null)
            {
                return precheckFailure;
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
        if (hasChanges && !rateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        // saveChanges: true so pressure-cooldown state mutated by world-state/pressure evaluation is
        // persisted even on pure-query turns (FilterAndCapAsync requires the caller to save).
        return ExecuteAsync(async session =>
        {
            var ctx = new TurnContext(request, effective, session);

            if (hasChanges)
            {
                var commitFailure = await CommitChangesAsync(ctx);
                if (commitFailure != null)
                {
                    return commitFailure;
                }
            }

            await RefreshInvolvedEntitiesAsync(ctx);
            await IncludePartyAsync(ctx);
            await IncludeWorldStateAsync(ctx);
            await IncludeFullNpcDetailAsync(ctx);
            await IncludeFullSceneDetailAsync(ctx);

            return Finalize(ctx, rateLimiter);
        }, saveChanges: true);
    }

    /// <summary>Static request validation that needs no session. Returns null when the request is valid.</summary>
    private static Task<ToolResult<TurnResult>>? ValidateChanges(TakeTurnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Narrative))
        {
            return ToolArgumentErrors.Missing<TurnResult>(
                "narrative",
                "Provide a short summary of what happened for the event log when Changes are provided.",
                toolName: "take_turn");
        }

        if (request.Changes!.Length > 50)
        {
            return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary: $"Commit rejected: Too many changes in a single batch ({request.Changes.Length}). Maximum allowed is 50."));
        }

        var duplicationConflict = SideEffectDuplicationGuard.FindConflict(request.Changes);
        if (duplicationConflict != null)
        {
            return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.InvalidArgument,
                Summary: $"Commit rejected: {duplicationConflict}"));
        }

        return null;
    }

    /// <summary>Stages the batch, logs the narrative event, composes reminders, and saves. Returns a failure result or null on success.</summary>
    private async Task<ToolResult<TurnResult>?> CommitChangesAsync(TurnContext ctx)
    {
        var request = ctx.Request!;
        var changes = request.Changes!;

        var commitResult = await _repository.StageChangesAsync(ctx.Session, changes, ctx.Campaign);
        if (!commitResult.Success)
        {
            var errorMsg = "NO CHANGES WERE SAVED — the entire batch was rolled back because at least one " +
                           "change failed validation. Fix the error(s) below and resend the FULL batch " +
                           "(not just the failed item).\n" + string.Join("\n", commitResult.Summary);
            return new ToolResult<TurnResult>(false, new TurnResult(), Summary: errorMsg, Error: "ValidationError");
        }

        var result = ctx.Result;
        result.Committed = true;
        result.ChangesProcessed = commitResult.ChangesProcessed;
        result.Summary = commitResult.Summary;
        result.InvolvedEntities = commitResult.InvolvedEntities;
        result.EntityCollisions = commitResult.EntityCollisions;
        result.NarrativeReminder = commitResult.NarrativeReminder;

        var commitTime = await _repository.GetTimeAsync(ctx.Session, ctx.Campaign);
        var sceneEvent = new Event
        {
            Id = "events/" + Guid.NewGuid(),
            CampaignName = ctx.Campaign,
            Summary = request.Narrative!,
            Category = EventCategory.SceneCommit,
            Involved = commitResult.InvolvedEntities,
            DayLogged = (int)commitTime.TotalDaysElapsed,
            RelatedEntityId = ExtractPrimaryActor(commitResult.InvolvedEntities)
        };

        await _repository.LogEventAsync(ctx.Session, sceneEvent, ctx.Campaign);

        ComposeReminders(changes, result);

        await ctx.Session.SaveChangesAsync();
        return null;
    }

    /// <summary>Extracts the primary actor (typically the first player character) from involved entities.</summary>
    private static string? ExtractPrimaryActor(List<string> involvedEntities)
    {
        if (involvedEntities == null || involvedEntities.Count == 0)
            return null;

        // Prefer player character (chars/pc or any chars/ that's not an NPC)
        // For simplicity, just return the first chars/ entity (usually the acting character)
        return involvedEntities.FirstOrDefault(id =>
            !string.IsNullOrEmpty(id) && id.StartsWith("chars/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Appends commit-hygiene reminders (missing narrative event, missing PoI detail) without discarding earlier reminders.</summary>
    private static void ComposeReminders(WorldChange[] changes, TurnResult result)
    {
        var hasCombatMutation = changes.Any(c => c is HpChange or RulesetAction or StatusChange);
        var hasNarrativeEvent = changes.Any(c => c is EventOccurred);
        if (hasCombatMutation && !hasNarrativeEvent)
        {
            AppendReminder(result,
                "This commit included combat/status changes but no 'event' ($type: event). " +
                "Add an EventOccurred to record the narrative beat.");
        }

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
                AppendReminder(result,
                    $"This commit moved a character to {string.Join(", ", uncoveredMoves)} alongside an Important/Core event " +
                    "but recorded no location detail. If the spot matters, add poiName/poiDetails.");
            }
        }
    }

    private static void AppendReminder(TurnResult result, string reminder) =>
        result.NarrativeReminder = result.NarrativeReminder is null
            ? reminder
            : result.NarrativeReminder + " " + reminder;

    /// <summary>
    /// Fetches lightweight summaries for refreshed entities. Explicitly requested extras are queued
    /// before auto-involved IDs, so the 6-NPC/3-scene caps never silently drop something the caller asked for.
    /// </summary>
    private async Task RefreshInvolvedEntitiesAsync(TurnContext ctx)
    {
        var request = ctx.Request;
        var result = ctx.Result;

        const int NpcCap = 6;
        const int SceneCap = 3;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var npcCandidates = new List<string>();
        var sceneCandidates = new List<string>();

        void AddCandidate(string id, bool explicitlyRequested)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                return;
            }

            if (id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase))
            {
                npcCandidates.Add(id);
            }
            else if (id.StartsWith(CanonicalId.Locations, StringComparison.OrdinalIgnoreCase))
            {
                sceneCandidates.Add(id);
            }
            else if (explicitlyRequested)
            {
                Warn(ctx, $"Refresh skipped for '{id}': not a '{CanonicalId.Characters}' or '{CanonicalId.Locations}' id.");
            }
        }

        foreach (var id in request?.ExtraCharacterIds ?? [])
        {
            AddCandidate(id, explicitlyRequested: true);
        }

        foreach (var id in request?.ExtraLocationIds ?? [])
        {
            AddCandidate(id, explicitlyRequested: true);
        }

        if (request?.AutoRefreshInvolved != false)
        {
            foreach (var id in result.InvolvedEntities)
            {
                AddCandidate(id, explicitlyRequested: false);
            }
        }

        var truncatedIds = npcCandidates.Skip(NpcCap).Concat(sceneCandidates.Skip(SceneCap)).ToList();
        if (truncatedIds.Count > 0)
        {
            result.RefreshTruncatedIds = truncatedIds;
        }

        var scenesToFetch = sceneCandidates.Take(SceneCap).ToList();
        if (scenesToFetch.Count > 0)
        {
            result.Scenes = [];
            foreach (var locationId in scenesToFetch)
            {
                try
                {
                    var summary = await _repository.BuildSceneSummaryAsync(ctx.Session, locationId, ctx.Campaign);
                    if (summary != null)
                    {
                        result.Scenes.Add(summary);
                    }
                    else
                    {
                        Warn(ctx, $"Scene refresh: '{locationId}' not found.");
                    }
                }
                catch (Exception ex)
                {
                    Warn(ctx, $"Scene refresh failed for '{locationId}': {ex.Message}", ex);
                }
            }
        }

        var npcsToFetch = npcCandidates.Take(NpcCap).ToList();
        if (npcsToFetch.Count > 0)
        {
            result.Npcs = [];
            foreach (var charId in npcsToFetch)
            {
                try
                {
                    var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, charId, ctx.Campaign);
                    if (summary != null)
                    {
                        result.Npcs.Add(summary);
                    }
                    else
                    {
                        Warn(ctx, $"NPC refresh: '{charId}' not found.");
                    }
                }
                catch (Exception ex)
                {
                    Warn(ctx, $"NPC refresh failed for '{charId}': {ex.Message}", ex);
                }
            }
        }
    }

    private async Task IncludePartyAsync(TurnContext ctx)
    {
        if (ctx.Request?.IncludeParty != true)
        {
            return;
        }

        try
        {
            var party = await ctx.Session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == ctx.Campaign && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            var partyMembers = new List<PartyMemberView>();
            foreach (var member in party)
            {
                try
                {
                    var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, member.Id, ctx.Campaign);
                    if (summary != null)
                    {
                        partyMembers.Add(new PartyMemberView(member, summary.Equipped, summary.Carried));
                    }
                }
                catch (Exception ex)
                {
                    Warn(ctx, $"Party summary failed for '{member.Id}': {ex.Message}", ex);
                }
            }

            if (partyMembers.Count > 0)
            {
                ctx.Result.Party = partyMembers;
            }
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Party section failed: {ex.Message}", ex);
        }
    }

    private async Task IncludeWorldStateAsync(TurnContext ctx)
    {
        if (ctx.Request?.IncludeWorldState != true)
        {
            return;
        }

        try
        {
            var worldState = await _repository.BuildWorldStateAsync(ctx.Session, ctx.Campaign, ctx.Request.PartyLocationId, _pressureOrchestrator);
            // take_turn context uses fewer events (first 5) vs kickoff's full list
            ctx.Result.WorldState = new WorldStateView(
                worldState.Time,
                worldState.ActiveRumors,
                worldState.RecentEvents.Take(5),
                worldState.PartyLocation,
                worldState.WorldPressure,
                worldState.ActiveQuests,
                worldState.RelevantFactions,
                worldState.LastKnownTravel,
                worldState.SuggestedCommitExamples
            );
            ctx.Result.WorldState.WorldPressureItems = worldState.WorldPressureItems;
        }
        catch (Exception ex)
        {
            Warn(ctx, $"World-state section failed: {ex.Message}", ex);
        }
    }

    private async Task IncludeFullNpcDetailAsync(TurnContext ctx)
    {
        var characterId = ctx.Request?.FullDetailCharacterId;
        if (string.IsNullOrEmpty(characterId))
        {
            return;
        }

        try
        {
            var npc = await _repository.GetCharacterAsync(ctx.Session, characterId, ctx.Campaign);
            if (npc == null)
            {
                Warn(ctx, $"Full NPC detail: '{characterId}' not found.");
                return;
            }

            var heldItems = await ctx.Session.Query<Item>()
                .Where(i => i.HolderId == npc.Id && !i.IsArchived)
                .Customize(x => x.WaitForNonStaleResults())
                .ToListAsync();
            var equipped = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList();
            var carried = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList();

            var config = await _repository.GetCampaignConfigAsync(ctx.Session, ctx.Campaign);
            var npcEvents = await _repository.SelectRecentEventsAsync(ctx.Session, ctx.Campaign,
                config.EventContextBudgetNpc, involvedCharacterId: characterId);

            foreach (var ev in npcEvents)
            {
                _repository.SanitizeEvent(ev);
            }

            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, null, npcEvents);

            ctx.Result.FullNpcContext = new NpcContextView
            {
                Character = npc,
                Psychology = npc.Psychology ?? new PsychologyProfile(),
                Social = npc.Social ?? new SocialProfile(),
                Needs = npc.Needs ?? new NeedsProfile(),
                SystemStats = npc.SystemStats ?? new SystemExtension(),
                RecentInteractions = npcEvents,
                BehavioralSummary = behavioralSummary,
                KnownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>(),
                Equipped = equipped,
                Carried = carried
            };
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Full NPC detail failed for '{characterId}': {ex.Message}", ex);
        }
    }

    private async Task IncludeFullSceneDetailAsync(TurnContext ctx)
    {
        var locationId = ctx.Request?.FullDetailLocationId;
        if (string.IsNullOrEmpty(locationId))
        {
            return;
        }

        try
        {
            var scene = await _repository.GetSceneAsync(ctx.Session, locationId, ctx.Campaign, markVisited: false);
            if (scene != null)
            {
                ctx.Result.FullScene = scene;
            }
            else
            {
                Warn(ctx, $"Full scene detail: '{locationId}' not found.");
            }
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Full scene detail failed for '{locationId}': {ex.Message}", ex);
        }
    }

    private static ToolResult<TurnResult> Finalize(TurnContext ctx, RateLimiter rateLimiter)
    {
        var result = ctx.Result;

        var stats = rateLimiter.GetStatistics();
        if (stats != null)
        {
            result.RateLimitTokensRemaining = (int)stats.CurrentAvailablePermits;
        }

        var successMsg = result.Committed
            ? $"World updated with {result.ChangesProcessed} changes and fresh state echoed."
            : "State refreshed.";
        if (result.Warnings is { Count: > 0 })
        {
            successMsg += $" {result.Warnings.Count} warning(s) — see 'warnings'.";
        }

        return new ToolResult<TurnResult>(true, result, successMsg);
    }

    private void Warn(TurnContext ctx, string message, Exception? ex = null)
    {
        _logger.LogWarning(ex, "take_turn warning (campaign {Campaign}): {Message}", ctx.Campaign, message);
        (ctx.Result.Warnings ??= []).Add(message);
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
        [Description("Resulting hour of day (0-23, e.g. 6 for dawn, 12 for noon, 20 for evening). Required when using 'days'. Omit when using 'hours' — derived automatically.")]
        int? resultingHour = null,
        [Description("Alternative to days/resultingHour: hours to fast-forward from the CURRENT time (e.g. 8 for sleeping through the night, 4 for a half-day trek). The engine computes the resulting hour for you. Mutually exclusive with days/resultingHour.")]
        int? hours = null)
    {
        if (hours.HasValue)
        {
            if (hours.Value <= 0)
            {
                return ToolArgumentErrors.Missing<AdvanceResult>(
                    "hours",
                    "hours must be a positive number of hours to fast-forward. Use days+resultingHour for a multi-day skip instead.",
                    toolName: "advance_world");
            }

            if (days != 0 || resultingHour.HasValue)
            {
                return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "InvalidArgument",
                    Summary: "Pass either 'hours' OR 'days'+'resultingHour', not both."));
            }
        }
        else if (days <= 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest",
                Summary: "Cannot advance zero or a negative number of days. Use 'hours' instead for a sub-day/overnight span."));
        }
        else if (!resultingHour.HasValue)
        {
            return ToolArgumentErrors.Missing<AdvanceResult>(
                "resultingHour",
                "Required when using 'days' for a multi-day skip (0-23). Use 'hours' instead for a same-night/partial-day span, which derives the hour automatically.",
                toolName: "advance_world");
        }
        else if (resultingHour < 0 || resultingHour > 23)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "InvalidArgument",
                Summary: "resultingHour must be between 0 and 23."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var result = await _repository.AdvanceWorldAsync(session, days, resultingHour, effective, hours);

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
