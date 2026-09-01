using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
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

        /// <summary>Delta-mode only: NPCs considered for initiative this turn but not selected, who still
        /// carry a high-salience memory — populated by SelectAndEnrichInitiativeAsync, consumed by the
        /// Npcs/PartyDelta builders (MemoryHint field) and Finalize (QuerySuggestions).</summary>
        public Dictionary<string, string> MemoryHintsByNpcId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The TurnCursor loaded/created by DecideTurnModeAsync, tracked here so later steps
        /// (DetectAndApplyReseedTriggersAsync) can escalate Mode/reset the SAME tracked instance instead
        /// of re-fetching — the RavenDB session already has it in its first-level cache either way, but
        /// this avoids two code paths computing "is this a new cursor" independently.</summary>
        public TurnCursor Cursor { get; set; } = null!;

        /// <summary>Pre-commit (characterId,targetId) -> relationship value snapshot, taken before
        /// CommitChangesAsync applies any RelationshipChange in this batch. Used by
        /// DetectAndApplyReseedTriggersAsync to detect a band crossing without reconstructing the old
        /// value as "new - delta" (wrong once the handler's [-100,100] clamp or multiple RelationshipChange
        /// entries for the same pair in one batch are in play).</summary>
        public Dictionary<(string CharacterId, string TargetId), int> RelationshipBaselines { get; } =
            new();

        /// <summary>Campaign config loaded once by DecideTurnModeAsync — reused by later steps (e.g.
        /// ChangedNeedsKeys' significance threshold) instead of re-fetching.</summary>
        public CampaignConfig Config { get; set; } = null!;

        /// <summary>Set by DecideTurnModeAsync when the client repeats ForceFullReseed shortly after
        /// already being reseeded — appended to NarrativeReminder in Finalize (not in CommitChangesAsync,
        /// which unconditionally overwrites NarrativeReminder and is never reached by pure-query calls).</summary>
        public string? ReseedAdvisory { get; set; }
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

FULL/DELTA MODE (see 'mode' in the response): the campaign automatically alternates between full snapshots and delta-only responses to save tokens. On mode=full, includeParty/includeWorldState return complete Party/WorldState. On mode=delta (most calls), they return PartyDelta/WorldStateDelta instead — only what changed this turn, echoing the applied commit objects rather than full entity state; NPC summaries also drop appearance/behavioral-summary/gear fields that didn't change this turn, and KnownNeeds is filtered to needs that moved >= 2 points (pass leanMode=true to cap that at the top 2 movers, for long multi-NPC scenes). A full reseed happens periodically (server-configured, default every 40 turns) and escalates early — even mid-delta-run — on a major PC location change, a relationship shift crossing a ±40 band, a significant plot-thread beat (once at least 3 delta turns have elapsed since the last reseed), or a party-fingerprint mismatch (see below). It can also be forced any time with forceFullReseed=true — do this if your own context was just compacted/summarized, or at the start of a fresh session, so you aren't reasoning from a stale partial view. Full detail for anything not covered by a delta is always available via get_entity for a single character/location, or by setting includeParty/includeWorldState on this same take_turn call for broader state — check 'querySuggestions' in the response for concrete calls worth making (populated when entities were dropped by the refresh cap, or an NPC has an aging high-salience memory flagged via 'memoryHint'). Independent of mode, up to 2 NPCs per call carry RP-advisory initiative/memory ('initiative' field, memories compressed to topic+one-liner on delta turns) — one is a party companion when one is present — so you get a 'who might act/speak next' signal without an extra call.

DRIFT PROTECTION: every response carries 'partyFingerprint' (a readable ""charId:hp/maxHp@locationId"" list for the party). Pass it back as clientPartyFingerprint on your NEXT take_turn call, unchanged. If it doesn't match what the server computed, that means you missed or misread a prior delta — the server forces a full resync and flags it in the response, so you don't keep narrating from a stale mental model (e.g. treating a PC as still in a location they already left). You can also eyeball the fingerprint yourself each turn as a sanity check against your own understanding of the party's state.

Pure queries (no Changes): omit Changes, provide at least one refresh param, and the response will refresh specific entities without mutations. Examples: includeWorldState=true to get campaign state, includeParty=true to get party summaries, or extraCharacterIds=[id] to refresh specific NPCs. Check the 'warnings' array in the response for any section that could not be assembled.")]
    public Task<ToolResult<TurnResult>> TakeTurn(
        [Description("Bundled turn request: MUST contain EITHER (1) Changes with Narrative, OR (2) at least one refresh parameter. Passing neither will be rejected. Mutations: Changes+Narrative. Refresh params: AutoRefreshInvolved (default true), ExtraCharacterIds, ExtraLocationIds, IncludeWorldState, IncludeParty, FullDetailCharacterId, FullDetailLocationId.")]
        TakeTurnRequest request,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        var hasChanges = request?.Changes is { Length: > 0 };

        if (hasChanges && request!.MinutesElapsed is > 0)
        {
            ApplyMinutesElapsedFallback(request);
        }

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
                await SnapshotRelationshipBaselinesAsync(ctx);

                var commitFailure = await CommitChangesAsync(ctx);
                if (commitFailure != null)
                {
                    return commitFailure;
                }

                await DetectAndApplyReseedTriggersAsync(ctx);
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
            await RefreshPartyFingerprintAsync(ctx);

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
        var clientForced = ctx.Request?.ForceFullReseed == true;
        var repeatedForce = clientForced && cursor is { TurnsSinceReseed: < 2, ConsecutiveClientForcedReseeds: >= 1 };
        var driftDetected = DetectPartyFingerprintDrift(ctx, cursor);

        var mode =
            !config.DeltaModeEnabled ? TurnMode.Full :
            cursor == null ? TurnMode.Full :
            clientForced ? TurnMode.Full :
            cursor.ForcedFullReseedPending ? TurnMode.Full :
            driftDetected ? TurnMode.Full :
            cursor.TurnsSinceReseed >= config.DeltaModeReseedIntervalTurns ? TurnMode.Full :
            TurnMode.Delta;

        if (repeatedForce)
        {
            ctx.ReseedAdvisory = "Note: forceFullReseed was set again right after a prior reseed — " +
                "if you're missing specific state, get_entity targets it more cheaply than another full reseed.";
        }
        else if (driftDetected)
        {
            ctx.ReseedAdvisory = "Note: your clientPartyFingerprint didn't match the server's last-known party state — " +
                "forcing a full resync since you may have missed or misread a prior delta. Trust this response over your " +
                "own narrative model of the party.";
        }

        _logger.LogDebug(
            "take_turn mode decision (campaign {Campaign}): mode={Mode} turnsSinceReseed={TurnsSinceReseed} " +
            "reseedIntervalTurns={ReseedIntervalTurns} isNewCursor={IsNewCursor} clientForced={ClientForced} " +
            "forcedPending={ForcedPending} driftDetected={DriftDetected}",
            ctx.Campaign, mode, cursor?.TurnsSinceReseed ?? 0, config.DeltaModeReseedIntervalTurns,
            isNewCursor, clientForced, cursor?.ForcedFullReseedPending ?? false, driftDetected);

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
        turnCursor.ConsecutiveClientForcedReseeds = clientForced ? turnCursor.ConsecutiveClientForcedReseeds + 1 : 0;

        if (isNewCursor)
        {
            await ctx.Session.StoreAsync(turnCursor, turnCursor.Id);
        }

        ctx.Cursor = turnCursor;
        ctx.Config = config;
        ctx.Mode = mode;
        ctx.Result.Mode = mode;
    }

    /// <summary>
    /// Compares the client's echoed ClientPartyFingerprint (what it believes the party looked like as of
    /// the last response) against the server's LastPartyFingerprint (what the server actually sent last
    /// time). A mismatch means the client missed or misread a prior delta — logged as one of three
    /// distinct Debug outcomes (echo-absent/echo-match/echo-mismatch) so it's obvious at a glance whether
    /// this mechanism is even receiving echoes, since an LLM client can silently stop echoing an opaque-ish
    /// field. Absence is never treated as drift — only a genuine mismatch forces a reseed.
    /// </summary>
    private bool DetectPartyFingerprintDrift(TurnContext ctx, TurnCursor? cursor)
    {
        var clientValue = ctx.Request?.ClientPartyFingerprint;

        if (string.IsNullOrEmpty(clientValue))
        {
            _logger.LogDebug("take_turn party fingerprint (campaign {Campaign}): echo-absent", ctx.Campaign);
            return false;
        }

        if (string.IsNullOrEmpty(cursor?.LastPartyFingerprint))
        {
            // Nothing to compare against yet (first call, or server never computed one) - not a mismatch.
            _logger.LogDebug("take_turn party fingerprint (campaign {Campaign}): echo-absent (no prior server value)", ctx.Campaign);
            return false;
        }

        if (string.Equals(clientValue, cursor.LastPartyFingerprint, StringComparison.Ordinal))
        {
            _logger.LogDebug("take_turn party fingerprint (campaign {Campaign}): echo-match", ctx.Campaign);
            return false;
        }

        _logger.LogWarning(
            "take_turn party fingerprint MISMATCH (campaign {Campaign}): expected '{Expected}', client echoed '{Actual}' — forcing full reseed",
            ctx.Campaign, cursor.LastPartyFingerprint, clientValue);
        return true;
    }

    /// <summary>
    /// Readable fingerprint of current party (PC + companion) state: "charId:hp/maxHp@locationId" per
    /// member, sorted by ID for determinism. Deliberately readable rather than an opaque hash — an LLM
    /// client can sanity-check it against its own narrative model directly, not just detect a dropped
    /// response. Mirrors IncludePartyAsync's WaitForNonStaleResults customization so a checksum computed
    /// immediately after a commit reflects what was just written, not a stale index read.
    /// </summary>
    private async Task<string> ComputePartyFingerprintAsync(TurnContext ctx)
    {
        var party = await ctx.Session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
            .Where(c => c.CampaignName == ctx.Campaign && (c.IsPc || c.IsPartyCompanion))
            .ToListAsync();

        return string.Join(",", party
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => $"{c.Id}:{c.CurrentHp}/{c.MaxHp}@{c.CurrentLocationId ?? "?"}"));
    }

    /// <summary>
    /// Recomputes the party fingerprint as of the end of this call (post-commit if there was one), stores
    /// it on the cursor for next turn's drift check, and echoes it + WorldSequence in the result. Runs
    /// unconditionally (not just on mutation turns) so a pure-query call still keeps the drift check alive.
    /// </summary>
    private async Task RefreshPartyFingerprintAsync(TurnContext ctx)
    {
        var fingerprint = await ComputePartyFingerprintAsync(ctx);
        ctx.Cursor.LastPartyFingerprint = fingerprint;
        ctx.Result.PartyFingerprint = fingerprint;
        ctx.Result.WorldSequence = ctx.Cursor.WorldSequence;
    }

    /// <summary>
    /// Snapshots (characterId,targetId) -> current relationship value for every RelationshipChange in
    /// this batch, before CommitChangesAsync applies them — see TurnContext.RelationshipBaselines for why
    /// this can't be reconstructed as "new - delta" after the fact. Same session, so these loads are
    /// first-level-cache hits (CommitChangesAsync/RelationshipChangeHandler will load the same characters).
    /// </summary>
    private static async Task SnapshotRelationshipBaselinesAsync(TurnContext ctx)
    {
        foreach (var change in ctx.Request!.Changes!)
        {
            if (change is not RelationshipChange rel)
            {
                continue;
            }

            var key = (rel.CharacterId, rel.TargetId);
            if (ctx.RelationshipBaselines.ContainsKey(key))
            {
                continue;
            }

            var source = await ctx.Session.LoadAsync<Character>(rel.CharacterId);
            ctx.RelationshipBaselines[key] = source?.Social?.Relationships?.GetValueOrDefault(rel.TargetId, 0) ?? 0;
        }
    }

    /// <summary>
    /// Escalation floor + trigger set for the review's "force full reseed on triggers" ask, run after
    /// CommitChangesAsync (needs AppliedChanges + post-commit relationship values) and before the section
    /// builders. Escalates a turn that DecideTurnModeAsync already picked Delta for, up to Full, when a
    /// major location change, a large relationship shift, or a significant plot-thread beat happened THIS
    /// turn. Gated on TurnsSinceReseed >= 3 so a single early relationship/location beat in a long social
    /// scene doesn't defeat delta mode in exactly the case it exists for.
    /// </summary>
    private async Task DetectAndApplyReseedTriggersAsync(TurnContext ctx)
    {
        if (ctx.Mode != TurnMode.Delta || ctx.Cursor.TurnsSinceReseed < 3)
        {
            return;
        }

        if (!await AnyTriggerFiredAsync(ctx))
        {
            return;
        }

        _logger.LogDebug(
            "take_turn mode decision (campaign {Campaign}): escalated Delta->Full mid-turn (location/relationship/plot trigger, turnsSinceReseed was {TurnsSinceReseed})",
            ctx.Campaign, ctx.Cursor.TurnsSinceReseed);

        ctx.Cursor.TurnsSinceReseed = 0;
        ctx.Cursor.ForcedFullReseedPending = false;
        ctx.Cursor.ConsecutiveClientForcedReseeds = 0;
        ctx.Cursor.LastFullReseedUtc = DateTime.UtcNow;
        ctx.Mode = TurnMode.Full;
        ctx.Result.Mode = TurnMode.Full;
    }

    private async Task<bool> AnyTriggerFiredAsync(TurnContext ctx)
    {
        var checkedCharacterIds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        async Task<bool> IsPcAsync(string characterId)
        {
            if (checkedCharacterIds.TryGetValue(characterId, out var cached))
            {
                return cached;
            }

            var character = await ctx.Session.LoadAsync<Character>(characterId);
            var isPc = character?.IsPc == true;
            checkedCharacterIds[characterId] = isPc;
            return isPc;
        }

        foreach (var change in ctx.AppliedChanges)
        {
            switch (change)
            {
                case ActivityChange { UpdateLocation: true } ac:
                    if (await IsPcAsync(ac.CharacterId))
                    {
                        return true;
                    }
                    break;

                case TravelChange tc:
                    if (await IsPcAsync(tc.CharacterId))
                    {
                        return true;
                    }
                    break;

                case RelationshipChange rel when ctx.RelationshipBaselines.TryGetValue(
                    (rel.CharacterId, rel.TargetId), out var before):
                    var after = Math.Clamp(before + rel.Delta, -100, 100);
                    if (CrossedBand(before, after, 40))
                    {
                        return true;
                    }
                    break;

                case PlotThreadProgress ptp when Math.Abs(ptp.TensionDelta ?? 0) >= 25 || ptp.NewState != null:
                    return true;
            }
        }

        return false;
    }

    private static bool CrossedBand(int before, int after, int bandSize) =>
        before / bandSize != after / bandSize;

    /// <summary>
    /// Applies the request-level MinutesElapsed fallback to the first eligible change in the batch when
    /// no individual change already carries its own MinutesElapsed. Excludes RestChange/TravelChange,
    /// which advance time via their own hour fields (see WorldChangeDispatcher.ApplyMicroTimeNudgeAsync).
    /// Assigns to a single change (not every change) so the batch's stated duration isn't multiplied by
    /// the number of changes in it.
    /// </summary>
    private static void ApplyMinutesElapsedFallback(TakeTurnRequest request)
    {
        if (request.Changes is not { Length: > 0 } changes)
        {
            return;
        }

        if (changes.Any(c => c.MinutesElapsed is > 0))
        {
            return;
        }

        var target = changes.FirstOrDefault(c => c is not RestChange and not TravelChange);
        if (target != null)
        {
            target.MinutesElapsed = request.MinutesElapsed;
        }
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

        ctx.Cursor.WorldSequence++;

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
            Summary = Truncate(request.Narrative!, MaxNarrativeSummaryLength),
            Category = EventCategory.SceneCommit,
            Importance = request.NarrativeImportance ?? MemoryImportance.Important,
            // Every touched entity type (characters, locations, factions, quests, items) stays in
            // Involved — there's no dedicated field for factions/quests/items, and pressure
            // contributors (e.g. FactionRecentEventPressureContributor) already scan Involved for
            // those; location-scoped queries already fall back to Involved too (see
            // CampaignRepository's location-filtered event queries), so splitting locations into
            // RelatedLocationIds bought nothing but an extra field to keep in sync.
            Involved = commitResult.InvolvedEntities.Where(id => !string.IsNullOrEmpty(id)).ToList(),
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
    /// autoRefreshInvolved/Mode — so take_turn alone (without a separate drill-down call) still carries a "who might
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
                ctx.InitiativeByNpcId[npc.Id] = ctx.Mode == TurnMode.Delta ? CompressForDelta(enrichment) : enrichment;
            }
            catch (Exception ex)
            {
                Warn(ctx, $"Initiative enrichment failed for '{npc.Id}': {ex.Message}", ex);
            }
        }

        if (ctx.Mode == TurnMode.Delta)
        {
            foreach (var npc in pool.Values)
            {
                if (selected.Any(s => s.Id == npc.Id))
                {
                    continue;
                }

                var topMemory = npc.Psychology?.Memories.Values
                    .Where(m => m.Salience >= HighSalienceThreshold)
                    .OrderByDescending(m => m.Salience)
                    .FirstOrDefault();

                if (topMemory != null)
                {
                    ctx.MemoryHintsByNpcId[npc.Id] =
                        $"{npc.Name} still has a high-salience memory '{topMemory.Topic}' — consider get_entity/recall_history if the conversation drifts toward it.";
                }
            }
        }
    }

    private const double HighSalienceThreshold = 0.75;

    private const int MaxNarrativeSummaryLength = 500;

    /// <summary>Trims a full-mode NpcInitiativeEnrichment down to the delta-mode wire shape: memories
    /// compressed to topic + one-line detail (see CompressedMemory) instead of full MemoryNode objects —
    /// likely the largest field in a delta response otherwise, per review recommendation 3.</summary>
    private static NpcInitiativeEnrichment CompressForDelta(NpcInitiativeEnrichment enrichment) => enrichment with
    {
        RelevantMemories = [],
        CompressedMemories = enrichment.RelevantMemories
            .Select(m => new CompressedMemory(m.Topic, Truncate(m.Details, 140)))
            .ToList()
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";

    /// <summary>
    /// Enrich (above) has a persisted side effect — it marks surfaced initiative candidates as consumed
    /// on the campaign doc via IInitiativeSuppressionStore, so the same candidate won't resurface next
    /// time (here or via get_entity) — so an enrichment that never reaches the model is worse than a no-op:
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

        foreach (var scene in ctx.Result.Scenes ?? [])
        {
            foreach (var presentNpc in scene.PresentNPCs)
            {
                alreadySurfaced.Add(presentNpc.Id);
            }
        }

        foreach (var npcId in ctx.InitiativeByNpcId.Keys)
        {
            if (alreadySurfaced.Contains(npcId))
            {
                continue;
            }

            try
            {
                var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, npcId, ctx.Campaign, BuildTrim(ctx, npcId));
                if (summary != null)
                {
                    summary.Initiative = ctx.InitiativeByNpcId[npcId];
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
    /// True if this change could have altered the given character's narrative appearance (the fields
    /// <see cref="ShouldStripUnchangedAppearance"/> strips on mode=delta). Same explicit-switch convention
    /// as <see cref="AffectsGearOrStats"/> — appearance is comparatively static, so most delta turns won't
    /// touch it at all.
    /// </summary>
    private static bool AffectsAppearance(WorldChange change, string characterId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            CharacterUpdate cu => eq.Equals(cu.CharacterId, characterId) &&
                (cu.AppearanceOverride != null || cu.TagsToAdd is { Count: > 0 } || cu.TagsToRemove is { Count: > 0 } ||
                 cu.FeaturesToAdd is { Count: > 0 } || cu.FeaturesToRemove is { Count: > 0 }),
            CharacterCreate cc => eq.Equals(cc.CharacterId, characterId),
            _ => false
        };
    }

    private bool ShouldStripUnchangedAppearance(TurnContext ctx, string characterId) =>
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsAppearance(c, characterId));

    /// <summary>
    /// True if this change could have altered the given character's mood or activity (the fields that
    /// gate BehavioralSummary regeneration on mode=delta — the summary is derived from these plus recent
    /// events, so it's stale/unchanged whenever neither moved).
    /// </summary>
    private static bool AffectsMoodOrActivity(WorldChange change, string characterId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            MoodChange mc => eq.Equals(mc.CharacterId, characterId),
            ActivityChange ac => eq.Equals(ac.CharacterId, characterId) && ac.NewActivity != null,
            _ => false
        };
    }

    private bool ShouldSkipBehavioralSummary(TurnContext ctx, string characterId) =>
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsMoodOrActivity(c, characterId));

    /// <summary>
    /// On mode=delta, returns the set of need names that moved >= config's NeedsChangeSignificanceThreshold
    /// points this turn for this character — BuildNpcSummaryAsync uses this to filter KnownNeeds down to
    /// what's actually driving behavior right now instead of re-sending the full needs dict every call.
    /// Null (no filtering) on Full mode.
    /// </summary>
    private static IReadOnlyCollection<string>? ChangedNeedsKeys(TurnContext ctx, string characterId)
    {
        if (ctx.Mode != TurnMode.Delta)
        {
            return null;
        }

        var threshold = ctx.Config?.NeedsChangeSignificanceThreshold ?? 2f;
        var eq = StringComparer.OrdinalIgnoreCase;
        IEnumerable<string> movers = ctx.AppliedChanges
            .OfType<NeedChange>()
            .Where(nc => eq.Equals(nc.CharacterId, characterId) && Math.Abs(nc.Delta) >= threshold)
            .GroupBy(nc => nc.Need, eq)
            .Select(g => (Need: g.Key, MaxAbsDelta: g.Max(nc => Math.Abs(nc.Delta))))
            .OrderByDescending(x => x.MaxAbsDelta)
            .Select(x => x.Need);

        if (ctx.Request?.LeanMode == true)
        {
            movers = movers.Take(2);
        }

        return movers.ToList();
    }

    private CampaignRepository.NpcSummaryTrim BuildTrim(TurnContext ctx, string characterId) => new(
        StripAppearance: ShouldStripUnchangedAppearance(ctx, characterId),
        SkipBehavioralSummary: ShouldSkipBehavioralSummary(ctx, characterId),
        StripGear: ShouldStripUnchangedGear(ctx, characterId),
        NeedsKeysToInclude: ChangedNeedsKeys(ctx, characterId));

    /// <summary>
    /// True if this change could have altered the given location's own descriptive state (the fields
    /// <see cref="ApplyLocationDeltaTrim"/> strips on mode=delta), or represents someone newly arriving
    /// there this turn — in which case the client needs the full room description even though the
    /// Location document itself wasn't edited. Mirrors the per-character Affects* convention above.
    /// </summary>
    private static bool AffectsLocationDetail(WorldChange change, string locationId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            LocationUpdate lu => eq.Equals(lu.LocationId, locationId),
            ActivityChange ac => ac.UpdateLocation && eq.Equals(ac.NewLocationId, locationId),
            TravelChange tc => eq.Equals(tc.DestinationLocationId, locationId),
            _ => false
        };
    }

    private bool ShouldStripUnchangedLocationDetail(TurnContext ctx, string locationId) =>
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsLocationDetail(c, locationId));

    /// <summary>
    /// Trims a scene's LocationDetailView (already an immutable wire-record, detached from the tracked
    /// RavenDB entity via LocationDetailView.From — no risk of the trim being mistaken for real data and
    /// persisted) for a delta turn that didn't touch this location: id/name/type/parent/danger/faction
    /// survive (cheap, and combat/faction-relevant even when static), everything else (description, exits,
    /// POIs, ambient crowd, tags, metadata, recently-departed, climate) resets to its unset default. The
    /// client already has the full picture from the last full reseed or a prior delta that changed it;
    /// get_entity/get_scene always returns the complete current value regardless of mode.
    /// </summary>
    private static LocationDetailView ApplyLocationDeltaTrim(LocationDetailView loc) => loc with
    {
        Description = "",
        Exits = [],
        PointsOfInterest = [],
        PointOfInterestDetails = [],
        AmbientCrowd = null,
        LastVisitedDay = null,
        RecentlyDeparted = [],
        Metadata = [],
        CurrentState = null,
        VisualTags = [],
        DistinctiveFeatures = [],
        ClimateZone = null
    };

    /// <summary>
    /// Applies the SAME delta-mode trim decision as BuildTrim/BuildNpcSummaryAsync to a scene-embedded
    /// NpcPresenceSummary (Scenes[].PresentNPCs) — one source of truth for "did this NPC's
    /// appearance/behavior/needs/gear change this turn" shared between the Npcs[] and Scenes[] shapes,
    /// rather than a second independent implementation. Applied as a post-process here (not threaded into
    /// SceneNpcPresenceFactory) because that factory is also used by get_entity's location detail path,
    /// which has no delta-mode concept — keeping it mode-agnostic avoids a "forgot to pass Full" class of bug.
    /// A no-op in Full mode (get_entity's only mode; take_turn's periodic/forced Full).
    /// </summary>
    private NpcPresenceSummary ApplyDeltaTrim(TurnContext ctx, NpcPresenceSummary npc)
    {
        if (ctx.Mode != TurnMode.Delta)
        {
            return npc;
        }

        var trim = BuildTrim(ctx, npc.Id);
        var knownNeeds = trim.NeedsKeysToInclude != null
            ? npc.KnownNeeds.Where(kv => trim.NeedsKeysToInclude.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
            : npc.KnownNeeds;
        // Same filter as KnownNeeds: the reference-text descriptors only need to travel alongside the
        // need values that are actually moving this turn — the client already has the rest from the
        // last full reseed, and re-sending the full campaign-wide descriptor dict for every present NPC
        // every delta turn is pure repeated boilerplate (verified via token-budget measurement).
        var needDescriptors = trim.NeedsKeysToInclude != null
            ? npc.NeedDescriptors.Where(kv => trim.NeedsKeysToInclude.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
            : npc.NeedDescriptors;

        return npc with
        {
            CurrentAppearance = trim.StripAppearance ? null : npc.CurrentAppearance,
            VisualTags = trim.StripAppearance ? null : npc.VisualTags,
            DistinctiveFeatures = trim.StripAppearance ? null : npc.DistinctiveFeatures,
            BehavioralSummary = trim.SkipBehavioralSummary ? null : npc.BehavioralSummary,
            KnownNeeds = knownNeeds,
            NeedDescriptors = needDescriptors,
            SystemStats = trim.StripGear ? null : npc.SystemStats,
            EquippedItems = trim.StripGear ? null : npc.EquippedItems,
            CarriedItems = trim.StripGear ? null : npc.CarriedItems,
            RelevantMemories = npc.RelevantMemories is { Count: > 0 } ? [] : npc.RelevantMemories,
            CompressedMemories = npc.RelevantMemories is { Count: > 0 }
                ? npc.RelevantMemories.Select(m => new CompressedMemory(m.Topic, Truncate(m.Details, 140))).ToList()
                : null
        };
    }

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
                            .Select(npc => ApplyDeltaTrim(ctx, npc))
                            .ToList();
                        if (ShouldStripUnchangedLocationDetail(ctx, locationId))
                        {
                            summary.Location = ApplyLocationDeltaTrim(summary.Location);
                        }
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
                    var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, charId, ctx.Campaign, BuildTrim(ctx, charId));
                    if (summary != null)
                    {
                        summary.Initiative = ctx.InitiativeByNpcId.GetValueOrDefault(charId);
                        summary.MemoryHint = ctx.MemoryHintsByNpcId.GetValueOrDefault(charId);
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
                        Initiative = initiative,
                        MemoryHint = ctx.MemoryHintsByNpcId.GetValueOrDefault(member.Id)
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

        DedupeScenesCoveredByFullScene(result);
        DedupeNpcsCoveredByScenes(result);
        DedupeRumorsCoveredByWorldState(result);
        PopulateQuerySuggestions(result);

        if (!string.IsNullOrEmpty(ctx.ReseedAdvisory))
        {
            AppendReminder(result, ctx.ReseedAdvisory);
        }

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

    /// <summary>
    /// Review recommendation 4: "explicit querySuggestions" — models respond more reliably to a concrete
    /// suggested call than to silently noticing something's thin and re-querying on their own. Built from
    /// signals already computed this turn (RefreshTruncatedIds, MemoryHint) rather than a new heuristic
    /// pass, so this stays cheap and stays in sync with what actually got dropped/hinted.
    /// </summary>
    private static void PopulateQuerySuggestions(TurnResult result)
    {
        var suggestions = new List<string>();

        foreach (var id in result.RefreshTruncatedIds ?? [])
        {
            suggestions.Add($"get_entity {id}");
        }

        foreach (var npc in result.Npcs ?? [])
        {
            if (npc.MemoryHint != null)
            {
                suggestions.Add($"get_entity {npc.CharacterId} (full psychology + memories)");
            }
        }

        foreach (var delta in result.PartyDelta ?? [])
        {
            if (delta.MemoryHint != null)
            {
                suggestions.Add($"get_entity {delta.EntityId} (full psychology + memories)");
            }
        }

        if (suggestions.Count > 0)
        {
            result.QuerySuggestions = suggestions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// FullScene (IncludeFullSceneDetailAsync, driven by FullDetailLocationId) fetches the requested
    /// location via GetSceneAsync independently of RefreshInvolvedEntitiesAsync's Scenes[] pass — so
    /// when that same location is also auto-refreshed into Scenes[] (e.g. it's the destination of this
    /// turn's travel change), the location's exits/rumors/recent-events/NPC roster gets sent twice on
    /// the wire, once trimmed (Scenes[]) and once at full detail (FullScene). FullScene is always the
    /// richer copy (untrimmed, plus items/memories/stats the Scenes[] entry strips), so the Scenes[]
    /// entry for that location is pure duplication and safe to drop.
    /// </summary>
    private static void DedupeScenesCoveredByFullScene(TurnResult result)
    {
        if (result.FullScene?.Location?.Id is not { } fullSceneLocationId
            || result.Scenes is not { Count: > 0 } scenes)
        {
            return;
        }

        var remaining = scenes
            .Where(s => !fullSceneLocationId.Equals(s.Location?.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        result.Scenes = remaining.Count > 0 ? remaining : null;
    }

    /// <summary>
    /// An NPC present in a refreshed Scenes[].PresentNPCs entry (NpcPresenceSummary) already carries
    /// everything the parallel Npcs[] entry (NpcSummaryView) would — see RefreshInvolvedEntitiesAsync,
    /// which builds the two independently with no cross-check — so a duplicate top-level Npcs[] entry
    /// for the same id is redundant wire content. Drops it, merging any Initiative context onto the
    /// surviving NpcPresenceSummary first so nothing is lost. In practice this merge is a no-op:
    /// SceneNpcPresenceFactory always computes initiative enrichment for every present NPC, so the
    /// scene-side entry already has equal-or-richer initiative data — the merge is a defensive
    /// safety net, not the expected path.
    /// </summary>
    private static void DedupeNpcsCoveredByScenes(TurnResult result)
    {
        if (result.Npcs is not { Count: > 0 } npcs)
        {
            return;
        }

        var sceneNpcIds = new HashSet<string>(
            (result.Scenes ?? []).SelectMany(s => s.PresentNPCs.Select(n => n.Id))
                .Concat((result.FullScene?.PresentNPCs ?? []).Select(n => n.Id)),
            StringComparer.OrdinalIgnoreCase);

        if (sceneNpcIds.Count == 0)
        {
            return;
        }

        var toDrop = npcs.Where(n => sceneNpcIds.Contains(n.CharacterId)).ToList();
        if (toDrop.Count == 0)
        {
            return;
        }

        NpcPresenceSummary MergeInitiative(NpcPresenceSummary n, NpcSummaryView dropped) =>
            n.Id.Equals(dropped.CharacterId, StringComparison.OrdinalIgnoreCase)
                && n.BehavioralTension == 0
                && (n.ActiveInitiatives?.Count ?? 0) == 0
                && n.TurnIntent == null
                ? n with
                {
                    BehavioralTension = dropped.Initiative!.BehavioralTension,
                    ActiveInitiatives = dropped.Initiative.ActiveInitiatives,
                    RelevantMemories = dropped.Initiative.RelevantMemories,
                    TurnIntent = dropped.Initiative.TurnIntent
                }
                : n;

        foreach (var dropped in toDrop)
        {
            if (dropped.Initiative == null)
            {
                continue;
            }

            foreach (var scene in result.Scenes ?? [])
            {
                scene.PresentNPCs = scene.PresentNPCs.Select(n => MergeInitiative(n, dropped)).ToList();
            }

            if (result.FullScene != null)
            {
                result.FullScene.PresentNPCs = result.FullScene.PresentNPCs.Select(n => MergeInitiative(n, dropped)).ToList();
            }
        }

        result.Npcs = npcs.Except(toDrop).ToList();
    }

    /// <summary>
    /// WorldStateView.ActiveRumors and Scenes[]/FullScene's LocalRumors both come from the same
    /// region-scoped QueryRumorsAsync call keyed off the same regionId (party location's parent, or the
    /// scene's own location — normally identical), so on a call returning both, LocalRumors is usually a
    /// near-total subset of ActiveRumors in a second wire shape. WorldState is the less frequently present
    /// section (gated behind IncludeWorldState/reseed cadence) so it's kept; the overlap is dropped from
    /// the scene-local lists instead.
    /// </summary>
    private static void DedupeRumorsCoveredByWorldState(TurnResult result)
    {
        if (result.WorldState?.ActiveRumors is not { } activeRumors)
        {
            return;
        }

        var worldRumorIds = new HashSet<string>(
            activeRumors.Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);
        if (worldRumorIds.Count == 0)
        {
            return;
        }

        foreach (var scene in result.Scenes ?? [])
        {
            scene.LocalRumors = scene.LocalRumors.Where(r => !worldRumorIds.Contains(r.Id)).ToList();
        }

        if (result.FullScene != null)
        {
            result.FullScene.LocalRumors = result.FullScene.LocalRumors
                .Where(r => !worldRumorIds.Contains(r.Id))
                .ToList();
        }
    }

    private void Warn(TurnContext ctx, string message, Exception? ex = null)
    {
        _logger.LogWarning(ex, "take_turn warning (campaign {Campaign}): {Message}", ctx.Campaign, message);
        (ctx.Result.Warnings ??= []).Add(message);
    }

    // A single advance_world call runs every simulation rule exactly once for the whole span, no
    // matter how long it is, so an over-large skip is both a silent loss of simulation fidelity and an
    // unbounded calendar roll from one mistyped argument (hours:100000 is eleven in-world years). These
    // caps are deliberately generous — a season-long montage still fits in one call — and the error
    // text tells the caller to split rather than to give up.
    private const int MaxAdvanceDays = 365;
    private const int MaxAdvanceHours = 24 * 30;

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

            if (hours.Value > MaxAdvanceHours)
            {
                return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "InvalidArgument",
                    Summary: $"hours must be at most {MaxAdvanceHours} ({MaxAdvanceHours / 24} days). " +
                             "For a longer jump use 'days', and split genuinely epic skips across several calls " +
                             "so the simulation actually runs for each span."));
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
        else if (days > MaxAdvanceDays)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "InvalidArgument",
                Summary: $"days must be at most {MaxAdvanceDays}. Split a longer skip across several calls so the " +
                         "simulation actually runs for each span rather than collapsing decades into one tick."));
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
