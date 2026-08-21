using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
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

        /// <summary>Full vs delta response mode, decided once per call from the campaign's TurnCursor.</summary>
        public TurnMode Mode { get; set; } = TurnMode.Full;

        /// <summary>WorldChanges applied this turn — the caller's own Changes[] plus any ambient simulation
        /// deltas (needs/memory decay) that ran synchronously because a commit crossed a day boundary.
        /// Empty for pure-query calls. This is the source of truth for delta-mode section builders.</summary>
        public IReadOnlyList<WorldChange> AppliedChanges { get; set; } = [];

        /// <summary>Persisted ambient simulation narrative text from this turn (see CommitResult.AmbientNarrativeSummaries).</summary>
        public IReadOnlyList<string> AmbientNarrativeSummaries { get; set; } = [];

        /// <summary>NPCs selected this call for RP-initiative/memory enrichment (capped, see
        /// SelectAndEnrichInitiativeAsync), keyed by character ID so Npcs/Party/PartyDelta section
        /// builders can attach the same computed enrichment without recomputing it per section.</summary>
        public Dictionary<string, NpcInitiativeEnrichment> InitiativeByNpcId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIFIED TURN TOOL: Call this at the end of any narrative beat (combat, conversation, discovery) for atomic mutations + bundled fresh state in one round-trip.

🚨 *** CRITICAL CONSTRAINT: MUST HAVE EITHER CHANGES OR A REFRESH PARAM *** 🚨
You MUST pass EITHER (1) Changes with a Narrative summary, OR (2) at least one refresh parameter (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, or fullDetailLocationId). Passing neither (empty call with no refresh param) will be rejected. This prevents wasted no-op calls.

🚨 *** CRITICAL — REQUIRED FIELD: '$type' *** 🚨
Every single change object in the changes[] array MUST include a '$type' field (the polymorphic discriminator — see WorldChange's own description for the full list of valid values). This is NOT OPTIONAL — it is REQUIRED for every change. If ANY object lacks '$type', the entire batch will fail to deserialize and be rejected.

One take_turn call carries optional mutations (Changes+Narrative) and optional refresh params, and returns the commit outcome + fresh entity summaries in one response — no separate query-before/query-after calls needed.

AUTO-REFRESH enabled by default (autoRefreshInvolved: true): the response includes lightweight summaries of any entities touched by the commit, capped at 6 NPCs and 3 scenes (explicitly requested extraCharacterIds/extraLocationIds are always served first). Opt out with autoRefreshInvolved: false for bulk/seeding commits.

FULL/DELTA MODE (see 'mode' in the response): the campaign automatically alternates between full snapshots and delta-only responses to save tokens. On mode=full, includeParty/includeWorldState return complete Party/WorldState. On mode=delta (most calls), they return PartyDelta/WorldStateDelta instead — only what changed this turn, echoing the applied commit objects rather than full entity state. A full reseed happens periodically (server-configured) and can be forced any time with forceFullReseed=true — do this if your own context was just compacted/summarized, or at the start of a fresh session, so you aren't reasoning from a stale partial view. Full detail for anything not covered by a delta is always available via get_entity/get_scene/get_world_state. Independent of mode, up to 2 NPCs per call carry RP-advisory initiative/memory ('initiative' field) — one is a party companion when one is present — so you get a 'who might act/speak next' signal even without calling get_scene.

Pure queries (no Changes): omit Changes, provide at least one refresh param, and the response will refresh specific entities without mutations. Examples: includeWorldState=true to get campaign state, includeParty=true to get party summaries, or extraCharacterIds=[id] to refresh specific NPCs. Check the 'warnings' array in the response for any section that could not be assembled.")]
    public Task<ToolResult<TurnResult>> TakeTurn(
        [Description("Bundled turn request: MUST contain EITHER (1) Changes with Narrative, OR (2) at least one refresh parameter. Passing neither will be rejected. Mutations: Changes+Narrative. Refresh params: AutoRefreshInvolved (default true), ExtraCharacterIds, ExtraLocationIds, IncludeWorldState, IncludeParty, FullDetailCharacterId, FullDetailLocationId.")]
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
                                   !string.IsNullOrEmpty(request.FullDetailLocationId) ||
                                   request.ForceFullReseed;

            if (!hasRefreshParams)
            {
                return Task.FromResult(new ToolResult<TurnResult>(
                    false,
                    Error: ToolErrors.InvalidArgument,
                    Summary: "This take_turn call has no Changes and no refresh parameters (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, fullDetailLocationId, forceFullReseed). Did you mean to commit world changes? Pass at least one refresh param if this is a pure-query call."));
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

            await DecideTurnModeAsync(ctx);

            if (hasChanges)
            {
                var commitFailure = await CommitChangesAsync(ctx);
                if (commitFailure != null)
                {
                    return commitFailure;
                }
            }

            if (ctx.Request?.AutoRefreshInvolved != false)
            {
                await SelectAndEnrichInitiativeAsync(ctx);
            }

            await RefreshInvolvedEntitiesAsync(ctx);
            await IncludePartyAsync(ctx);
            await EnsureInitiativeSurfacedAsync(ctx);
            await IncludeWorldStateAsync(ctx);
            await IncludeFullNpcDetailAsync(ctx);
            await IncludeFullSceneDetailAsync(ctx);

            return Finalize(ctx, rateLimiter);
        }, saveChanges: true);
    }

    /// <summary>
    /// Decides Full vs Delta for this call and persists the updated TurnCursor (via the already-open
    /// session — no extra SaveChangesAsync needed, ExecuteAsync's saveChanges:true covers it). Absence of
    /// a cursor document means take_turn has never been called for this campaign — naturally Full.
    /// Imprecision (e.g. a retried commit double-incrementing the counter) is accepted; this is a
    /// token-budget heuristic, not a correctness guarantee.
    /// </summary>
    private async Task DecideTurnModeAsync(TurnContext ctx)
    {
        var campaignSession = new CampaignSession(ctx.Session, ctx.Campaign);
        var config = await _repository.GetCampaignConfigAsync(campaignSession);
        var cursor = await _repository.GetTurnCursorAsync(campaignSession);

        var isNewCursor = cursor == null;
        var mode =
            !config.DeltaModeEnabled ? TurnMode.Full :
            cursor == null ? TurnMode.Full :
            ctx.Request?.ForceFullReseed == true ? TurnMode.Full :
            cursor.ForcedFullReseedPending ? TurnMode.Full :
            cursor.TurnsSinceReseed >= config.DeltaModeReseedIntervalTurns ? TurnMode.Full :
            TurnMode.Delta;

        var turnCursor = cursor ?? new TurnCursor { Id = _keys.StateTurnCursor(ctx.Campaign), CampaignName = ctx.Campaign };
        if (mode == TurnMode.Full)
        {
            turnCursor.TurnsSinceReseed = 0;
            turnCursor.ForcedFullReseedPending = false;
            turnCursor.LastFullReseedUtc = DateTime.UtcNow;
        }
        else
        {
            turnCursor.TurnsSinceReseed++;
        }

        if (isNewCursor)
        {
            await ctx.Session.StoreAsync(turnCursor, turnCursor.Id);
        }

        ctx.Mode = mode;
        ctx.Result.Mode = mode;
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

        var commitResult = await _repository.StageChangesAsync(new CampaignSession(ctx.Session, ctx.Campaign), changes);
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
        ctx.AppliedChanges = changes.Concat(commitResult.AmbientDeltas).ToList();
        ctx.AmbientNarrativeSummaries = commitResult.AmbientNarrativeSummaries;

        var commitTime = await _repository.GetTimeAsync(new CampaignSession(ctx.Session, ctx.Campaign));
        var sceneEvent = new Event
        {
            Id = "events/" + Guid.NewGuid(),
            CampaignName = ctx.Campaign,
            Summary = request.Narrative!,
            Category = EventCategory.SceneCommit,
            Involved = commitResult.InvolvedEntities,
            DayLogged = (int)commitTime.TotalDaysElapsed,
            Details = ExtractEventDetails(changes),
            RelatedEntityId = ExtractPrimaryActor(commitResult.InvolvedEntities)
        };

        await _repository.LogEventAsync(ctx.Session, sceneEvent, ctx.Campaign);

        // Calculate novelty score after event is persisted with semantic vector
        var (similarity, noveltyHint) = await EventNoveltyAdvisor.ScoreAsync(
            ctx.Session, sceneEvent, ctx.Campaign, _logger);
        sceneEvent.NoveltyScore = similarity;
        if (!string.IsNullOrEmpty(noveltyHint))
        {
            result.NarrativeReminder = result.NarrativeReminder is null
                ? noveltyHint
                : result.NarrativeReminder + " " + noveltyHint;
        }

        ComposeReminders(changes, result);

        await ctx.Session.SaveChangesAsync();
        return null;
    }

    /// <summary>Extracts structured details from all mutations for event enrichment.</summary>
    private static IDictionary<string, object>? ExtractEventDetails(WorldChange[] changes)
    {
        var details = new Dictionary<string, object>();

        var itemTransfers = new List<object>();
        var damageDealt = new List<object>();
        var statusesApplied = new List<object>();
        var resourcesSpent = new List<object>();
        var relationshipChanges = new List<object>();
        var locationsVisited = new List<object>();
        var needsChanged = new List<object>();
        var questsProgressed = new List<object>();
        var factsDiscovered = new List<object>();

        foreach (var change in changes)
        {
            switch (change)
            {
                case ItemTransfer it:
                    itemTransfers.Add(new ItemTransferDetail(it.ItemId, it.ToHolderId));
                    break;

                case HpChange hp:
                    if (hp.Delta != 0)
                    {
                        damageDealt.Add(new DamageDealtDetail(hp.CharacterId, hp.Delta));
                    }
                    break;

                case StatusChange sc:
                    if (sc.Effect != null)
                    {
                        statusesApplied.Add(new StatusAppliedDetail(
                            sc.CharacterId,
                            sc.Effect.Name,
                            sc.Effect.Category?.ToString()));
                    }
                    else if (!string.IsNullOrEmpty(sc.Status))
                    {
                        statusesApplied.Add(new StatusAppliedDetail(sc.CharacterId, sc.Status));
                    }
                    break;

                case ResourceChange rc:
                    if (rc.Delta != 0)
                    {
                        resourcesSpent.Add(new ResourceSpentDetail(rc.CharacterId, rc.PoolName, rc.Delta));
                    }
                    break;

                case RelationshipChange rel:
                    if (rel.Delta != 0)
                    {
                        relationshipChanges.Add(new RelationshipChangeDetail(rel.CharacterId, rel.TargetId, rel.Delta));
                    }
                    break;

                case ActivityChange ac:
                    if (ac.UpdateLocation && !string.IsNullOrEmpty(ac.NewLocationId))
                    {
                        locationsVisited.Add(new LocationVisitedDetail(ac.CharacterId, ac.NewLocationId, ac.PoiName));
                    }
                    break;

                case NeedChange nc:
                    if (nc.Delta != 0)
                    {
                        needsChanged.Add(new NeedChangedDetail(nc.CharacterId, nc.Need, nc.Delta));
                    }
                    break;

                case QuestProgress qp:
                    if (!string.IsNullOrEmpty(qp.QuestId))
                    {
                        questsProgressed.Add(new QuestProgressedDetail(qp.QuestId, qp.NewState));
                    }
                    break;

                case PlotThreadClueDiscovered ptc:
                    if (!string.IsNullOrEmpty(ptc.PlotThreadId) && !string.IsNullOrEmpty(ptc.ClueId))
                    {
                        factsDiscovered.Add(new PlotThreadFactDetail(ptc.PlotThreadId, ptc.ClueId));
                    }
                    break;

                case RulesetAction ra:
                    // Combat/skill check actions
                    factsDiscovered.Add(new RulesetActionFactDetail(ra.CharacterId, ra.ActionType.ToString(), ra.ActionName));
                    break;
            }
        }

        // Add non-empty sections to details
        if (itemTransfers.Count > 0) details["itemTransfers"] = itemTransfers;
        if (damageDealt.Count > 0) details["damageDealt"] = damageDealt;
        if (statusesApplied.Count > 0) details["statusesApplied"] = statusesApplied;
        if (resourcesSpent.Count > 0) details["resourcesSpent"] = resourcesSpent;
        if (relationshipChanges.Count > 0) details["relationshipChanges"] = relationshipChanges;
        if (locationsVisited.Count > 0) details["locationsVisited"] = locationsVisited;
        if (needsChanged.Count > 0) details["needsChanged"] = needsChanged;
        if (questsProgressed.Count > 0) details["questsProgressed"] = questsProgressed;
        if (factsDiscovered.Count > 0) details["factsDiscovered"] = factsDiscovered;

        return details.Count > 0 ? details : null;
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

    private const int InitiativeCap = 2;

    /// <summary>
    /// Surfaces RP-advisory initiative/memory for up to 2 NPCs this call, independent of includeParty/
    /// autoRefreshInvolved/Mode — so take_turn alone (without a get_scene call) still carries a "who might
    /// act/speak next" signal. Candidate pool: NPCs present at the party's current location, unioned with
    /// any NPCs this turn's changes touched (fallback when location isn't resolvable). Selection: one
    /// guaranteed slot for a randomly-chosen party companion if the pool has one, remaining slot(s) filled
    /// by other NPCs. Results are cached on ctx.InitiativeByNpcId so Npcs/Party/PartyDelta section builders
    /// can attach them without recomputing.
    /// </summary>
    private async Task SelectAndEnrichInitiativeAsync(TurnContext ctx)
    {
        var pool = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);

        var locationId = ctx.Request?.PartyLocationId;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            var pc = await ctx.Session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == ctx.Campaign && c.IsPc && c.CurrentLocationId != null)
                .FirstOrDefaultAsync();
            locationId = pc?.CurrentLocationId;
        }

        if (!string.IsNullOrWhiteSpace(locationId))
        {
            try
            {
                var present = await _repository.GetPresentNpcsAsync(ctx.Session, locationId, ctx.Campaign);
                foreach (var npc in present)
                {
                    pool.TryAdd(npc.Id, npc);
                }
            }
            catch (Exception ex)
            {
                Warn(ctx, $"Initiative candidate lookup failed for location '{locationId}': {ex.Message}", ex);
            }
        }

        var touchedCharacterIds = ctx.AppliedChanges
            .SelectMany(_repository.ExtractInvolvedEntityIds)
            .Where(id => id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var id in touchedCharacterIds)
        {
            if (pool.ContainsKey(id))
            {
                continue;
            }

            var npc = await _repository.GetCharacterAsync(new CampaignSession(ctx.Session, ctx.Campaign), id);
            if (npc != null)
            {
                pool.TryAdd(id, npc);
            }
        }

        if (pool.Count == 0)
        {
            return;
        }

        var companions = pool.Values.Where(c => c.IsPartyCompanion).ToList();
        var selected = new List<Character>();
        if (companions.Count > 0)
        {
            selected.Add(companions[Random.Shared.Next(companions.Count)]);
        }

        // Prefer a non-companion NPC for the remaining slot(s) — the companion slot above already
        // guarantees companion coverage, so this spreads the signal to the environment instead of
        // potentially filling both slots with companions.
        foreach (var candidate in pool.Values)
        {
            if (selected.Count >= InitiativeCap)
            {
                break;
            }

            if (candidate.IsPc || candidate.IsPartyCompanion || selected.Any(s => s.Id == candidate.Id))
            {
                continue;
            }

            selected.Add(candidate);
        }

        // Fallback: if the pool has no (more) non-companion NPCs, fill remaining slot(s) from whoever's left.
        foreach (var candidate in pool.Values)
        {
            if (selected.Count >= InitiativeCap)
            {
                break;
            }

            if (candidate.IsPc || selected.Any(s => s.Id == candidate.Id))
            {
                continue;
            }

            selected.Add(candidate);
        }

        foreach (var npc in selected)
        {
            try
            {
                var enrichment = await _repository.EnrichNpcInitiativeAsync(
                    ctx.Session, npc, ctx.Campaign, "take_turn", includeTensionBreakdown: false);
                ctx.InitiativeByNpcId[npc.Id] = enrichment;
            }
            catch (Exception ex)
            {
                Warn(ctx, $"Initiative enrichment failed for '{npc.Id}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Enrich (above) has a persisted side effect — it marks surfaced initiative candidates as consumed
    /// on the campaign doc via IInitiativeSuppressionStore, so the same candidate won't resurface next
    /// time (here or in get_scene) — so an enrichment that never reaches the model is worse than a no-op:
    /// it silently burns candidates for nothing. Npcs/Party/PartyDelta only attach the cached enrichment
    /// to NPCs they already happen to include (via InvolvedEntities/extraCharacterIds, or includeParty).
    /// This guarantees every NPC actually selected in SelectAndEnrichInitiativeAsync ends up visible
    /// somewhere in the response — appending a lightweight NpcSummaryView to Npcs if it isn't already
    /// covered by Npcs/Party/PartyDelta — so the work done (and the suppression state spent) always pays off.
    /// </summary>
    private async Task EnsureInitiativeSurfacedAsync(TurnContext ctx)
    {
        if (ctx.InitiativeByNpcId.Count == 0)
        {
            return;
        }

        var alreadySurfaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in ctx.Result.Npcs ?? [])
        {
            alreadySurfaced.Add(n.CharacterId);
        }

        foreach (var p in ctx.Result.Party ?? [])
        {
            alreadySurfaced.Add(p.Id);
        }

        foreach (var d in ctx.Result.PartyDelta ?? [])
        {
            alreadySurfaced.Add(d.EntityId);
        }

        foreach (var npcId in ctx.InitiativeByNpcId.Keys)
        {
            if (alreadySurfaced.Contains(npcId))
            {
                continue;
            }

            try
            {
                var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, npcId, ctx.Campaign);
                if (summary != null)
                {
                    summary.Initiative = ctx.InitiativeByNpcId[npcId];
                    if (ShouldStripUnchangedGear(ctx, npcId))
                    {
                        summary.Equipped = null;
                        summary.Carried = null;
                    }
                    (ctx.Result.Npcs ??= []).Add(summary);
                }
            }
            catch (Exception ex)
            {
                Warn(ctx, $"Initiative surfacing failed for '{npcId}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// True if this change could have altered the given character's equipment or ruleset stats
    /// (the fields <see cref="ShouldStripUnchangedGear"/> strips on mode=delta). Mirrors the type-level
    /// precedent in <see cref="SideEffectDuplicationGuard"/>: an explicit switch over the handful of
    /// WorldChange types that actually touch gear/stats, rather than the broad
    /// ExtractInvolvedEntityIds used for pressure/InvolvedEntities tracking — that one also matches
    /// purely narrative changes (activity, event, mood) that reference the character without
    /// changing anything worth re-sending.
    /// </summary>
    private static bool AffectsGearOrStats(WorldChange change, string characterId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            ItemTransfer it => eq.Equals(it.ToHolderId, characterId),
            ItemEquip ie => eq.Equals(ie.CharacterId, characterId),
            ItemUnequip iu => eq.Equals(iu.CharacterId, characterId),
            HpChange hp => eq.Equals(hp.CharacterId, characterId),
            StatusChange sc => eq.Equals(sc.CharacterId, characterId),
            StatusRemove sr => eq.Equals(sr.CharacterId, characterId),
            ResourceChange rc => eq.Equals(rc.CharacterId, characterId),
            LevelUpChange lc => eq.Equals(lc.CharacterId, characterId),
            CharacterUpdate cu => cu.SystemStats != null && eq.Equals(cu.CharacterId, characterId),
            RulesetAction ra => eq.Equals(ra.CharacterId, characterId) || ra.TargetIds.Any(t => eq.Equals(t, characterId)),
            CharacterCreate cc => eq.Equals(cc.CharacterId, characterId),
            _ => false
        };
    }

    /// <summary>
    /// On mode=delta, blanks EquippedItems/CarriedItems/SystemStats for an NPC whose gear/stats
    /// weren't touched this turn — the client already has last-known values from the last full
    /// reseed (or a prior delta that did change them). Full mode always leaves data untouched.
    /// </summary>
    private bool ShouldStripUnchangedGear(TurnContext ctx, string characterId) =>
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsGearOrStats(c, characterId));

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
                        summary.PresentNPCs = summary.PresentNPCs
                            .Select(npc => ShouldStripUnchangedGear(ctx, npc.Id)
                                ? npc with { SystemStats = null, EquippedItems = null, CarriedItems = null }
                                : npc)
                            .ToList();
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
                        summary.Initiative = ctx.InitiativeByNpcId.GetValueOrDefault(charId);
                        if (ShouldStripUnchangedGear(ctx, charId))
                        {
                            summary.Equipped = null;
                            summary.Carried = null;
                        }
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

            if (ctx.Mode == TurnMode.Full)
            {
                var partyMembers = new List<PartyMemberView>();
                foreach (var member in party)
                {
                    try
                    {
                        var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, member.Id, ctx.Campaign);
                        if (summary != null)
                        {
                            partyMembers.Add(new PartyMemberView(
                                CharacterDetailView.From(member),
                                summary.Equipped,
                                summary.Carried,
                                ctx.InitiativeByNpcId.GetValueOrDefault(member.Id)));
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
            else
            {
                var deltas = new List<EntityChangeDelta>();
                foreach (var member in party)
                {
                    var memberChanges = ctx.AppliedChanges
                        .Where(c => _repository.ExtractInvolvedEntityIds(c).Contains(member.Id, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    var hasInitiative = ctx.InitiativeByNpcId.TryGetValue(member.Id, out var initiative);

                    if (memberChanges.Count == 0 && !hasInitiative)
                    {
                        continue;
                    }

                    deltas.Add(new EntityChangeDelta
                    {
                        EntityId = member.Id,
                        Name = member.Name,
                        Changes = memberChanges,
                        Initiative = initiative
                    });
                }

                if (deltas.Count > 0)
                {
                    ctx.Result.PartyDelta = deltas;
                }
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

            if (ctx.Mode == TurnMode.Full)
            {
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
            else
            {
                var newEvents = new List<string>();
                if (!string.IsNullOrWhiteSpace(ctx.Request?.Narrative))
                {
                    newEvents.Add(ctx.Request!.Narrative!);
                }

                newEvents.AddRange(ctx.AmbientNarrativeSummaries);

                ctx.Result.WorldStateDelta = new WorldStateDeltaView
                {
                    Time = worldState.Time,
                    WorldPressure = worldState.WorldPressure,
                    RumorChanges = ctx.AppliedChanges.OfType<RumorEvolves>().ToList(),
                    QuestChanges = ctx.AppliedChanges.OfType<QuestProgress>().ToList(),
                    FactionReputationChanges = ctx.AppliedChanges.OfType<FactionReputationChange>().ToList(),
                    FactionStateChanges = ctx.AppliedChanges.OfType<FactionStateChange>().ToList(),
                    NewEvents = newEvents.Count > 0 ? newEvents : null
                };
            }
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
            var npc = await _repository.GetCharacterAsync(new CampaignSession(ctx.Session, ctx.Campaign), characterId);
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

            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(ctx.Session, ctx.Campaign));
            var npcEvents = await _repository.SelectRecentEventsAsync(ctx.Session, ctx.Campaign,
                config.EventContextBudgetNpc, involvedCharacterId: characterId);

            foreach (var ev in npcEvents)
            {
                JsonSanitizer.Sanitize(ev);
            }

            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, null, npcEvents);

            ctx.Result.FullNpcContext = new NpcContextView
            {
                Character = CharacterDetailView.From(npc),
                RecentInteractions = npcEvents.Select(EventSummaryView.From).ToList(),
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
            var scene = await _repository.GetSceneAsync(new CampaignSession(ctx.Session, ctx.Campaign), locationId, markVisited: false);
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

            // advance_world can run simulation ticks outside the take_turn pipeline — force the next
            // take_turn call to Full so ambient drift from this skip isn't missed by delta mode.
            var turnCursor = await _repository.GetTurnCursorAsync(new CampaignSession(session, effective));
            if (turnCursor == null)
            {
                await session.StoreAsync(
                    new TurnCursor { Id = _keys.StateTurnCursor(effective), CampaignName = effective, ForcedFullReseedPending = true },
                    _keys.StateTurnCursor(effective));
            }
            else
            {
                turnCursor.ForcedFullReseedPending = true;
            }

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
            var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));

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
