using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
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
        /// Empty for pure-query calls. This is the source of truth for delta-mode section builders' gating
        /// decisions (did *something* change this turn) — see <see cref="AmbientChanges"/> for the narrower
        /// set that's actually worth echoing back to the caller.</summary>
        public IReadOnlyList<WorldChange> AppliedChanges { get; set; } = [];

        /// <summary>Subset of <see cref="AppliedChanges"/> the caller did NOT itself submit this call — i.e.
        /// CommitResult.AmbientDeltas alone. PartyDelta echoes only this subset in its Changes field: the
        /// caller already has the change objects it just wrote in its own Changes[] (same rationale as
        /// <see cref="InvolvedEntityIds"/> not echoing caller-chosen IDs back), and echoing them isn't even a
        /// reliable receipt — a relative NeedChange delta echoed verbatim doesn't reflect server-side
        /// clamping, so it can't confirm what actually landed either. Only genuinely new, server-derived
        /// deltas are worth the bytes.</summary>
        public IReadOnlyList<WorldChange> AmbientChanges { get; set; } = [];

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

        /// <summary>Pre-commit characterId -> CurrentLocationId snapshot, taken before CommitChangesAsync
        /// applies any ActivityChange/TravelChange in this batch. Used by DetectAndApplyReseedTriggersAsync
        /// to tell an actual location transition apart from a same-location POI/activity update (both set
        /// UpdateLocation:true) — only the former should escalate to a full reseed.</summary>
        public Dictionary<string, string?> LocationBaselines { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Campaign config loaded once by DecideTurnModeAsync — reused by later steps (e.g.
        /// ChangedNeedsKeys' significance threshold) instead of re-fetching.</summary>
        public CampaignConfig Config { get; set; } = null!;

        /// <summary>Set by DecideTurnModeAsync when the client repeats ForceFullReseed shortly after
        /// already being reseeded — appended to NarrativeReminder in Finalize (not in CommitChangesAsync,
        /// which unconditionally overwrites NarrativeReminder and is never reached by pure-query calls).</summary>
        public string? ReseedAdvisory { get; set; }

        /// <summary>Set by ApplyInitiativeNudges when the same NPC has been re-nudged (NpcInitiativeNudge)
        /// too many times in a row before a previous nudge was actually consumed by a selection —
        /// appended to NarrativeReminder in Finalize, same treatment as ReseedAdvisory.</summary>
        public string? NudgeAdvisory { get; set; }

        /// <summary>Every entity ID touched by this turn's changes (chars/locations/items/quests/factions),
        /// used internally to drive RefreshInvolvedEntitiesAsync's auto-bundling into Npcs/Scenes and to
        /// tag the auto-logged SceneCommit event's Involved list. Not part of the response payload — the
        /// caller already knows every ID it just wrote in its own Changes[] (IDs are client-chosen, not
        /// server-generated), so echoing them back added no information.</summary>
        public List<string> InvolvedEntityIds { get; set; } = [];
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIFIED TURN TOOL: Call this at the end of any narrative beat (combat, conversation, discovery) for atomic mutations + bundled fresh state in one round-trip.

🚨 *** CRITICAL CONSTRAINT: MUST HAVE EITHER CHANGES OR A REFRESH PARAM *** 🚨
You MUST pass EITHER (1) Changes with a Narrative summary, OR (2) at least one refresh parameter (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, memoriesOnlyCharacterId, or fullDetailLocationId). Passing neither (empty call with no refresh param) will be rejected. This prevents wasted no-op calls.

Every change in changes[] MUST include '$type' (see WorldChange). Missing '$type' fails the batch.

One take_turn call carries optional mutations (Changes+Narrative) and optional refresh params, and returns the commit outcome + fresh entity summaries in one response — no separate query-before/query-after calls needed.

AUTO-REFRESH (default on): response includes lightweight summaries of entities touched by the commit, capped at 6 NPCs / 3 scenes (explicit extraCharacterIds/extraLocationIds served first). Opt out with autoRefreshInvolved: false for bulk/seeding commits.

FULL/DELTA MODE: see 'mode' in the response. mode=delta (the common case) returns PartyDelta/WorldStateDelta — only what changed, not full state. Use get_entity, includeParty/includeWorldState, or forceFullReseed=true to get anything a delta didn't cover; check 'querySuggestions' in the response for concrete follow-ups. Full mode-mechanics reference: get_help topic=take-turn-modes.

DRIFT PROTECTION: response carries 'partyFingerprint'; echo it back unchanged as clientPartyFingerprint on your next call. A mismatch means you missed a prior delta — the server forces a resync and flags it.

Pure queries (no Changes): omit Changes, provide at least one refresh param instead. Check 'warnings' in the response for anything that couldn't be assembled.")]
    public Task<ToolResult<TurnResult>> TakeTurn(
        [Description("Bundled turn request: MUST contain EITHER (1) Changes with Narrative, OR (2) at least one refresh parameter. Passing neither will be rejected. Mutations: Changes+Narrative. Refresh params: AutoRefreshInvolved (default true), ExtraCharacterIds, ExtraLocationIds, IncludeWorldState, IncludeParty, FullDetailCharacterId, MemoriesOnlyCharacterId, FullDetailLocationId.")]
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
                                   !string.IsNullOrEmpty(request.MemoriesOnlyCharacterId) ||
                                   request.ForceFullReseed;

            if (!hasRefreshParams)
            {
                return Task.FromResult(new ToolResult<TurnResult>(
                    false,
                    Error: ToolErrors.InvalidArgument,
                    Summary: "This take_turn call has no Changes and no refresh parameters (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, fullDetailLocationId, memoriesOnlyCharacterId, forceFullReseed). Did you mean to commit world changes? Pass at least one refresh param if this is a pure-query call."));
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
            AgePendingInitiativeNudges(ctx);

            if (hasChanges)
            {
                await SnapshotTurnBaselinesAsync(ctx);

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

            await StampPartyPresentLocationsAsync(ctx);
            await RefreshInvolvedEntitiesAsync(ctx);
            await IncludePartyAsync(ctx);
            await EnsureInitiativeSurfacedAsync(ctx);
            await IncludeWorldStateAsync(ctx);
            await IncludeFullNpcDetailAsync(ctx);
            await IncludeMemoriesOnlyAsync(ctx);
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

        var thrashWindow = Math.Max(1, config.ForcedReseedThrashWindow);
        var thrashThreshold = Math.Max(1, config.ForcedReseedThrashThreshold);

        turnCursor.RecentClientForcedFlags.Add(clientForced);
        if (turnCursor.RecentClientForcedFlags.Count > thrashWindow)
        {
            turnCursor.RecentClientForcedFlags.RemoveAt(0);
        }

        if (turnCursor.RecentClientForcedFlags.Count >= thrashThreshold)
        {
            var forcedCount = turnCursor.RecentClientForcedFlags.Count(f => f);
            if (forcedCount >= thrashThreshold)
            {
                var thrashAdvisory = $"Note: forceFullReseed was set on {forcedCount} of your last " +
                    $"{turnCursor.RecentClientForcedFlags.Count} take_turn calls. Full reseeds cost far more tokens " +
                    "per turn than delta mode — if you're unsure whether state changed, trust the delta response or " +
                    "call get_entity for the one thing you need instead of forcing a full resync every time.";
                ctx.ReseedAdvisory = ctx.ReseedAdvisory is null ? thrashAdvisory : ctx.ReseedAdvisory + " " + thrashAdvisory;
            }
        }

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
    /// Snapshots (characterId,targetId) -> current relationship value for every RelationshipChange, and
    /// characterId -> CurrentLocationId for every location-touching ActivityChange/TravelChange, in this
    /// batch, before CommitChangesAsync applies them — see TurnContext.RelationshipBaselines/
    /// LocationBaselines for why these can't be reconstructed after the fact. Same session, so these loads
    /// are first-level-cache hits (CommitChangesAsync's handlers will load the same characters).
    /// </summary>
    private static async Task SnapshotTurnBaselinesAsync(TurnContext ctx)
    {
        foreach (var change in ctx.Request!.Changes!)
        {
            switch (change)
            {
                case RelationshipChange rel:
                {
                    var key = (rel.CharacterId, rel.TargetId);
                    if (ctx.RelationshipBaselines.ContainsKey(key))
                    {
                        break;
                    }

                    var source = await ctx.Session.LoadAsync<Character>(rel.CharacterId);
                    ctx.RelationshipBaselines[key] =
                        source?.Social?.Relationships?.GetValueOrDefault(rel.TargetId, 0) ?? 0;
                    break;
                }

                case ActivityChange { UpdateLocation: true } ac when !ctx.LocationBaselines.ContainsKey(ac.CharacterId):
                {
                    var character = await ctx.Session.LoadAsync<Character>(ac.CharacterId);
                    ctx.LocationBaselines[ac.CharacterId] = character?.CurrentLocationId;
                    break;
                }

                case TravelChange tc when !ctx.LocationBaselines.ContainsKey(tc.CharacterId):
                {
                    var character = await ctx.Session.LoadAsync<Character>(tc.CharacterId);
                    ctx.LocationBaselines[tc.CharacterId] = character?.CurrentLocationId;
                    break;
                }
            }
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
                // Only an actual location transition counts as "major" — a same-location POI/activity
                // update also sets UpdateLocation:true (e.g. walking to a different street within the
                // same town) and shouldn't force a full reseed on its own.
                case ActivityChange { UpdateLocation: true } ac:
                    if (LocationChanged(ctx, ac.CharacterId, ac.NewLocationId) && await IsPcAsync(ac.CharacterId))
                    {
                        return true;
                    }
                    break;

                case TravelChange tc:
                    if (LocationChanged(ctx, tc.CharacterId, tc.DestinationLocationId) && await IsPcAsync(tc.CharacterId))
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

    /// <summary>True when the character's pre-commit location (see TurnContext.LocationBaselines) differs
    /// from the new location this change applies. Missing baseline (shouldn't happen — populated by
    /// SnapshotTurnBaselinesAsync for every location-touching change) is treated as "changed" so
    /// the trigger fails open toward a full reseed rather than silently under-escalating.</summary>
    private static bool LocationChanged(TurnContext ctx, string characterId, string? newLocationId) =>
        !ctx.LocationBaselines.TryGetValue(characterId, out var before)
        || !string.Equals(before, newLocationId, StringComparison.OrdinalIgnoreCase);

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

        var commitResult = await _repository.StageChangesAsync(new CampaignSession(ctx.Session, ctx.Campaign), changes, request.PartyLocationId);
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
        ctx.InvolvedEntityIds = commitResult.InvolvedEntities;
        result.EntityCollisions = commitResult.EntityCollisions;
        result.CommittedIds = commitResult.CommittedIds;
        result.NarrativeReminder = commitResult.NarrativeReminder;
        result.PhysicalStateNudges = commitResult.PhysicalStateNudges is { Count: > 0 } nudges ? nudges : null;
        ctx.AppliedChanges = changes.Concat(commitResult.AmbientDeltas).ToList();
        ctx.AmbientChanges = commitResult.AmbientDeltas;
        ctx.AmbientNarrativeSummaries = commitResult.AmbientNarrativeSummaries;
        ApplyInitiativeNudges(ctx);

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

    /// <summary>Ages pending NpcInitiativeNudges from prior calls, then registers this turn's own
    /// NpcInitiativeNudge WorldChanges as new pending entries — consumed later by
    /// SelectAndEnrichInitiativeAsync. Flags ctx.NudgeAdvisory when the same NPC is re-nudged before
    /// their previous nudge was ever consumed, config.InitiativeNudgeRepeatThreshold times in a row.</summary>
    private static void AgePendingInitiativeNudges(TurnContext ctx)
    {
        // Ages every call (commit or pure-query) — a nudge decays by elapsed take_turn calls, not just
        // ones that happen to carry a commit, or a run of pure-query calls would let it linger forever.
        var expired = new List<string>();
        foreach (var (npcId, pending) in ctx.Cursor.PendingInitiativeNudgesByEntityId)
        {
            pending.TurnsRemaining--;
            if (pending.TurnsRemaining <= 0)
            {
                expired.Add(npcId);
            }
        }
        foreach (var npcId in expired)
        {
            ctx.Cursor.PendingInitiativeNudgesByEntityId.Remove(npcId);
        }
    }

    /// <summary>Registers this turn's own NpcInitiativeNudge WorldChanges as new pending entries —
    /// consumed later by SelectAndEnrichInitiativeAsync. Flags ctx.NudgeAdvisory when the same NPC is
    /// re-nudged before their previous nudge was ever consumed, config.InitiativeNudgeRepeatThreshold
    /// times in a row. Only reachable from CommitChangesAsync (needs ctx.AppliedChanges), so nudges can
    /// only arrive via an actual commit — AgePendingInitiativeNudges above still ages them on pure-query
    /// calls in between.</summary>
    private static void ApplyInitiativeNudges(TurnContext ctx)
    {
        var unconsumedTurns = Math.Max(1, ctx.Config?.InitiativeNudgeUnconsumedTurns ?? 2);
        var repeatThreshold = Math.Max(1, ctx.Config?.InitiativeNudgeRepeatThreshold ?? 2);

        string? advisory = null;
        foreach (var nudge in ctx.AppliedChanges.OfType<NpcInitiativeNudge>())
        {
            int count;
            if (ctx.Cursor.PendingInitiativeNudgesByEntityId.ContainsKey(nudge.CharacterId))
            {
                count = ctx.Cursor.ConsecutiveUnconsumedNudgesByEntityId.GetValueOrDefault(nudge.CharacterId, 1) + 1;
            }
            else
            {
                count = 1;
            }
            ctx.Cursor.ConsecutiveUnconsumedNudgesByEntityId[nudge.CharacterId] = count;
            if (count >= repeatThreshold)
            {
                var thisAdvisory = $"Note: '{nudge.CharacterId}' has been nudged toward initiative " +
                    $"{count} times in a row without acting in between — make sure to actually follow " +
                    "through on the reaction (a mood shift, a line of dialogue, a consequence) before " +
                    "layering on another, for narrative realism.";
                advisory = advisory is null ? thisAdvisory : advisory + " " + thisAdvisory;
            }

            ctx.Cursor.PendingInitiativeNudgesByEntityId[nudge.CharacterId] = new PendingInitiativeNudge
            {
                Intensity = Math.Clamp(nudge.Intensity, 0f, 1f),
                Reason = nudge.Reason,
                TurnsRemaining = unconsumedTurns
            };
        }

        if (advisory != null)
        {
            ctx.NudgeAdvisory = ctx.NudgeAdvisory is null ? advisory : ctx.NudgeAdvisory + " " + advisory;
        }
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
                        locationsVisited.Add(new LocationVisitedDetail(ac.CharacterId, ac.NewLocationId, null));
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

    /// <summary>Appends commit-hygiene reminders (missing narrative event, missing PoI detail, likely-missed
    /// physical-state commit) without discarding earlier reminders.</summary>
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
                            && significantEventLocations.Contains(a.NewLocationId!)
                            && !poiCoveredLocations.Contains(a.NewLocationId!))
                .Select(a => a.NewLocationId!)
                .Distinct()
                .ToList();
            if (uncoveredMoves.Count > 0)
            {
                AppendReminder(result,
                    $"This commit moved a character to {string.Join(", ", uncoveredMoves)} alongside an Important/Core event " +
                    "but recorded no location detail. If the spot matters, add a location_update with materializePointOfInterest/poiDetails in the same commit.");
            }
        }

        if (changes.OfType<EventOccurred>().Any(e => e.ImpliesPersistentPhysicalChange == true)
            && !changes.Any(c => c is ItemEquip or ItemUnequip or ItemUpdate or StatusChange or StatusRemove
                or CharacterUpdate or ArchiveEntityChange { EntityType: ArchivableEntityType.Item }))
        {
            AppendReminder(result,
                "An event in this batch flagged impliesPersistentPhysicalChange=true, but the batch has no " +
                "matching take_turn change (item_equip/item_unequip/item_update/status/status_remove/character_update/" +
                "archive_entity). Add it now — otherwise the change silently reverts next scene.");
        }
    }

    private static void AppendReminder(TurnResult result, string reminder) =>
        result.NarrativeReminder = result.NarrativeReminder is null
            ? reminder
            : result.NarrativeReminder + " " + reminder;

    private const int InitiativeCap = 1;

    /// <summary>
    /// Surfaces RP-advisory initiative/memory for the single highest-priority NPC this call, independent of
    /// includeParty/autoRefreshInvolved/Mode — so take_turn alone (without a separate drill-down call) still
    /// carries a "who might act/speak next" signal. Candidate pool: NPCs present at the party's current
    /// location, unioned with any NPCs this turn's changes touched (fallback when location isn't
    /// resolvable). Selection is a small scheduler, not a random pick: InitiativeSelectionScorer ranks the
    /// pool by cheap need+momentum priority, then Campaign.RecentInitiativeSlotNpcIds applies a cooldown
    /// penalty to whoever won recently (CampaignConfig.InitiativeCooldownPenalty/Size) so one
    /// high-priority NPC can't hog every turn's slot — priority is the CPU-time-slice analogue, the
    /// cooldown is the anti-starvation term. Result is cached on ctx.InitiativeByNpcId so Npcs/Party/
    /// PartyDelta section builders can attach it without recomputing.
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

        var candidates = pool.Values.Where(c => !c.IsPc).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var config = await _repository.GetCampaignConfigAsync(new CampaignSession(ctx.Session, ctx.Campaign));
        var campaignDoc = await ctx.Session.LoadAsync<Campaign>(_keys.Meta(ctx.Campaign));
        var recentWinners = campaignDoc?.RecentInitiativeSlotNpcIds ?? [];

        // A pending NpcInitiativeNudge on a present candidate wins the slot outright — bypassing the
        // scorer and cooldown penalty — since it represents a specific reactive beat the DM already
        // judged worth surfacing, not routine rotation. Highest intensity wins if more than one present
        // candidate is nudged at once.
        Character? winner = candidates
            .Where(c => ctx.Cursor.PendingInitiativeNudgesByEntityId.ContainsKey(c.Id))
            .OrderByDescending(c => ctx.Cursor.PendingInitiativeNudgesByEntityId[c.Id].Intensity)
            .FirstOrDefault();
        string? nudgeReason = null;
        if (winner != null)
        {
            nudgeReason = ctx.Cursor.PendingInitiativeNudgesByEntityId[winner.Id].Reason;
            ctx.Cursor.PendingInitiativeNudgesByEntityId.Remove(winner.Id);
            ctx.Cursor.ConsecutiveUnconsumedNudgesByEntityId[winner.Id] = 0;
        }
        else
        {
            winner = candidates
                .OrderByDescending(c => InitiativeSelectionScorer.EstimatePriority(c, config, recentWinners))
                .First();
        }
        var selected = new List<Character> { winner };

        if (campaignDoc != null)
        {
            campaignDoc.RecentInitiativeSlotNpcIds.RemoveAll(id => id.Equals(winner.Id, StringComparison.OrdinalIgnoreCase));
            campaignDoc.RecentInitiativeSlotNpcIds.Insert(0, winner.Id);
            var cooldownSize = Math.Max(0, config.InitiativeCooldownSize);
            if (campaignDoc.RecentInitiativeSlotNpcIds.Count > cooldownSize)
            {
                campaignDoc.RecentInitiativeSlotNpcIds.RemoveRange(
                    cooldownSize, campaignDoc.RecentInitiativeSlotNpcIds.Count - cooldownSize);
            }
        }

        foreach (var npc in selected)
        {
            try
            {
                var enrichment = await _repository.EnrichNpcInitiativeAsync(
                    ctx.Session, npc, ctx.Campaign, "take_turn", includeTensionBreakdown: false);

                if (npc.Id.Equals(winner.Id, StringComparison.OrdinalIgnoreCase) && nudgeReason != null)
                {
                    var nudgeCandidate = new InitiativeCandidate(
                        Key: $"nudge:{npc.Id}",
                        NpcId: npc.Id,
                        Driver: InitiativeDriver.Disposition,
                        Urgency: MemoryUrgency.High,
                        FramingPrompt: nudgeReason,
                        Weight: 1.0);
                    enrichment = enrichment with
                    {
                        ActiveInitiatives = new[] { nudgeCandidate }.Concat(enrichment.ActiveInitiatives ?? []).ToList(),
                        TurnIntent = new TurnIntentSignal("npc", nudgeReason, MemoryUrgency.High)
                    };
                }

                ctx.InitiativeByNpcId[npc.Id] = ctx.Mode == TurnMode.Delta ? CompressForDelta(ctx, npc.Id, enrichment) : enrichment;
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

                if (topMemory == null)
                {
                    continue;
                }

                // Only re-surface this NPC's MemoryHint when the topic differs from what the client
                // was already told (tracked on TurnCursor, no extra query) — a stable high-salience
                // memory shouldn't re-cost tokens every delta call it sits unresolved.
                var alreadySurfaced = ctx.Cursor.SurfacedMemoryHintTopicsByEntityId.TryGetValue(npc.Id, out var lastTopic)
                    && string.Equals(lastTopic, topMemory.Topic, StringComparison.OrdinalIgnoreCase);
                if (alreadySurfaced)
                {
                    continue;
                }

                ctx.MemoryHintsByNpcId[npc.Id] =
                    $"{npc.Name} still has a high-salience memory '{topMemory.Topic}' — consider get_entity/recall_history if the conversation drifts toward it.";
                ctx.Cursor.SurfacedMemoryHintTopicsByEntityId[npc.Id] = topMemory.Topic;
            }
        }
    }

    private const double HighSalienceThreshold = 0.75;

    private const int MaxNarrativeSummaryLength = 500;

    /// <summary>Trims a full-mode NpcInitiativeEnrichment down to the delta-mode wire shape: memories
    /// compressed to topic + one-line detail (see CompressedMemory) instead of full MemoryNode objects —
    /// likely the largest field in a delta response otherwise, per review recommendation 3. Also gated by
    /// <see cref="CompressAndDedupeMemories"/> so a topic already sent to the client isn't repeated.</summary>
    private static NpcInitiativeEnrichment CompressForDelta(TurnContext ctx, string npcId, NpcInitiativeEnrichment enrichment) => enrichment with
    {
        RelevantMemories = [],
        CompressedMemories = CompressAndDedupeMemories(ctx, npcId, enrichment.RelevantMemories)
    };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";

    /// <summary>Compresses an NPC's currently-relevant memories to topic+one-liner, dropping any topic
    /// already sent to the client as of the last delta turn that surfaced it for this NPC — mirrors
    /// MemoryHintsByNpcId/SurfacedMemoryHintTopicsByEntityId's "don't re-cost tokens for a stable
    /// reading" gate (:978-990), which CompressedMemories never had despite being the largest single
    /// field in a typical delta scene NPC. The cursor entry is replaced (not unioned) with the current
    /// topic set every call, so a memory that drops out of relevance and later returns is treated as new
    /// again rather than permanently suppressed.</summary>
    private static List<CompressedMemory> CompressAndDedupeMemories(TurnContext ctx, string npcId, IReadOnlyList<MemoryNode> memories)
    {
        if (memories.Count == 0)
        {
            ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId.Remove(npcId);
            return [];
        }

        var alreadySurfaced = ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId.TryGetValue(npcId, out var priorTopics)
            ? new HashSet<string>(priorTopics, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = memories
            .Where(m => !alreadySurfaced.Contains(m.Topic))
            .Select(m => new CompressedMemory(m.Topic, Truncate(m.Details, 140)))
            .ToList();

        ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId[npcId] = memories.Select(m => m.Topic).ToList();
        return result;
    }

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
                var liveCharacter = await ctx.Session.LoadAsync<Character>(npcId);
                var liveNeeds = liveCharacter?.Needs?.ActiveNeeds ?? new Dictionary<string, float>();
                var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, npcId, ctx.Campaign, BuildTrim(ctx, npcId, liveNeeds));
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
            // SkillCheck/SavingThrow/ContestedCheck/OpposedCheck are pure rolls — both ruleset resolvers
            // (Dnd5e, Pf2e) never append to their `mutations` list for these action types (OpposedCheck
            // routes through the same ContestedCheck resolver), so they can't have touched gear/stats.
            // Attack/Spell/UseItem/Recovery can (damage, healing, resource/status changes), so those
            // still count.
            RulesetAction { ActionType: RulesetActionType.SkillCheck or RulesetActionType.SavingThrow
                or RulesetActionType.ContestedCheck or RulesetActionType.OpposedCheck } => false,
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
    private static IReadOnlyCollection<string>? ChangedNeedsKeys(TurnContext ctx, string characterId, IReadOnlyDictionary<string, float> liveNeeds)
    {
        if (ctx.Mode != TurnMode.Delta)
        {
            return null;
        }

        var threshold = ctx.Config?.NeedsChangeSignificanceThreshold ?? 2f;
        // Deliberately higher than the per-turn threshold — ordinary background ticking drifting a
        // few points over several turns isn't a meaningful state change on its own, only a materially
        // bigger cumulative swing is. See CampaignConfig.NeedsCumulativeDriftThreshold.
        var driftThreshold = ctx.Config?.NeedsCumulativeDriftThreshold ?? 10f;
        var eq = StringComparer.OrdinalIgnoreCase;

        // This turn's own NeedChange batch — catches a single large swing (including one that
        // introduces a need key never seen before, whose missing baseline below would otherwise
        // read as zero drift).
        var perTurnMovers = ctx.AppliedChanges
            .OfType<NeedChange>()
            .Where(nc => eq.Equals(nc.CharacterId, characterId) && Math.Abs(nc.Delta) >= threshold)
            .GroupBy(nc => nc.Need, eq)
            .Select(g => (Need: g.Key, MaxAbsDelta: g.Max(nc => Math.Abs(nc.Delta))))
            .OrderByDescending(x => x.MaxAbsDelta)
            .Select(x => x.Need);

        // Cumulative drift since this need's value was last actually surfaced — catches slow ticking
        // (e.g. hunger +1.5/turn) that never clears the per-turn threshold in any single NeedChange but
        // does add up to something worth mentioning over several turns.
        // A need with no recorded baseline (never surfaced before) contributes no drift here; it's
        // covered by perTurnMovers above instead, so a brand-new key doesn't spuriously "drift" the
        // first time it's ever seen.
        var baseline = ctx.Cursor.SurfacedNeedValuesByEntityId.TryGetValue(characterId, out var byNeed)
            ? byNeed
            : new Dictionary<string, float>(eq);
        var driftMovers = liveNeeds
            .Where(kv => baseline.TryGetValue(kv.Key, out var b) && Math.Abs(kv.Value - b) >= driftThreshold)
            .Select(kv => kv.Key);

        var movers = perTurnMovers.Concat(driftMovers).Distinct(eq).ToList();

        if (ctx.Request?.LeanMode == true)
        {
            movers = movers.Take(2).ToList();
        }

        if (!ctx.Cursor.SurfacedNeedValuesByEntityId.TryGetValue(characterId, out var toUpdate))
        {
            toUpdate = new Dictionary<string, float>(eq);
            ctx.Cursor.SurfacedNeedValuesByEntityId[characterId] = toUpdate;
        }
        foreach (var need in movers)
        {
            if (liveNeeds.TryGetValue(need, out var v))
            {
                toUpdate[need] = v;
            }
        }
        // Seed a baseline for every need not yet tracked (first time this NPC's needs are inspected
        // in Delta mode), so drift for those needs is measured correctly from here on rather than
        // staying permanently un-baselined.
        foreach (var kv in liveNeeds)
        {
            if (!toUpdate.ContainsKey(kv.Key))
            {
                toUpdate[kv.Key] = kv.Value;
            }
        }

        return movers;
    }

    private CampaignRepository.NpcSummaryTrim BuildTrim(TurnContext ctx, string characterId, IReadOnlyDictionary<string, float> liveNeeds) => new(
        StripAppearance: ShouldStripUnchangedAppearance(ctx, characterId),
        SkipBehavioralSummary: ShouldSkipBehavioralSummary(ctx, characterId),
        StripGear: ShouldStripUnchangedGear(ctx, characterId),
        NeedsKeysToInclude: ChangedNeedsKeys(ctx, characterId, liveNeeds));

    /// <summary>
    /// True if this change could have altered the given location's own descriptive state (the fields
    /// <see cref="ApplyLocationDeltaTrim"/> strips on mode=delta). Mirrors the per-character Affects*
    /// convention above.
    /// </summary>
    private static bool AffectsLocationDetail(WorldChange change, string locationId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            LocationUpdate lu => eq.Equals(lu.LocationId, locationId),
            // ActivityChangeHandler never writes to the Location document — it only moves the
            // *character* (CurrentLocationId). Materializing PoI state always goes through a
            // dedicated LocationUpdate (handled above), so an activity move alone never triggers a
            // full-location resend, even one that shifts within an already-known location. Genuine
            // cross-location arrivals go through TravelChange below, which still always resends.
            ActivityChange => false,
            TravelChange tc => eq.Equals(tc.DestinationLocationId, locationId),
            _ => false
        };
    }

    /// <summary>
    /// True if this change could have altered who/what is present at the given location this turn — a
    /// genuine arrival/departure or a crowd-interrupt that can promote ambient NPCs into a combatant.
    /// Deliberately narrower than "this change references the location id" (see the reflection-based
    /// default <see cref="IWorldChangeHandler.ExtractInvolvedEntities"/> and EventOccurredHandler's
    /// LocationId/RelatedLocationIds, which tag a location as "involved" for bookkeeping reasons that
    /// have nothing to do with presence). Used by <see cref="RefreshInvolvedEntitiesAsync"/> to decide
    /// whether an auto-added scene candidate is worth the full BuildSceneSummaryAsync assembly on a
    /// delta turn, not just whether it should be preloaded.
    /// </summary>
    private static bool AffectsScenePresence(WorldChange change, string locationId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            TravelChange tc => eq.Equals(tc.DestinationLocationId, locationId),
            ActivityChange ac => (ac.NewLocationId != null || ac.UpdateLocation) && eq.Equals(ac.NewLocationId, locationId),
            SceneInterruptCheck sic => eq.Equals(sic.LocationId, locationId),
            _ => false
        };
    }

    private bool ShouldStripUnchangedLocationDetail(TurnContext ctx, string locationId) =>
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsLocationDetail(c, locationId));

    /// <summary>
    /// Rumors change rarely, but BuildSceneSummaryAsync/CampaignRepository.cs:2917 always populates the
    /// scene's full LocalRumors list (id/subject/full text/state) regardless of mode, so a delta scene
    /// refresh resends every local rumor's full text on essentially every turn even when nothing about
    /// them changed. On delta turns, keep only the rumors this turn's changes actually touched (evolved
    /// or newly created); the client already has the rest from the last full reseed or a prior delta.
    /// Not gated by ShouldStripUnchangedLocationDetail — rumor state is independent of the location's own
    /// descriptive fields, so a location-detail change shouldn't force full rumors, and vice versa.
    /// </summary>
    private static IEnumerable<RumorSummary> ApplyRumorDeltaTrim(TurnContext ctx, IEnumerable<RumorSummary> rumors)
    {
        if (ctx.Mode != TurnMode.Delta)
        {
            return rumors;
        }

        var rumorList = rumors as IReadOnlyCollection<RumorSummary> ?? rumors.ToList();
        if (rumorList.Count == 0)
        {
            return rumorList;
        }

        var touchedRumorIds = new HashSet<string>(
            ctx.AppliedChanges.OfType<RumorEvolves>().Select(r => r.RumorId)
                .Concat(ctx.AppliedChanges.OfType<RumorCreate>().Select(r => r.RumorId)),
            StringComparer.OrdinalIgnoreCase);

        return touchedRumorIds.Count == 0
            ? []
            : rumorList.Where(r => touchedRumorIds.Contains(r.Id)).ToList();
    }

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
    private NpcPresenceSummary ApplyDeltaTrim(TurnContext ctx, NpcPresenceSummary npc, out bool fullyUnchanged)
    {
        if (ctx.Mode != TurnMode.Delta)
        {
            fullyUnchanged = false;
            return npc;
        }

        var trim = BuildTrim(ctx, npc.Id, npc.KnownNeeds);
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

        var compressedMemories = npc.RelevantMemories is { Count: > 0 }
            ? CompressAndDedupeMemories(ctx, npc.Id, npc.RelevantMemories)
            : null;

        // "Nothing new to say about this NPC this turn": every per-field trim gate stripped its field
        // and there's no fresh memory content riding along either. Consumed by
        // RefreshInvolvedEntitiesAsync to decide whether a scene-present NPC can shrink further, to an
        // id/name/roster-flags stub (see TurnCursor.SurfacedPresentNpcIdsByLocationId). Deliberately
        // excludes BehavioralTension/ActiveInitiatives/TurnIntent — those reflect current state, not
        // AppliedChanges, and are out of scope for this gate (see DELTA_PRESENCE_TRIM_PLAN.md non-goals).
        fullyUnchanged = trim.StripAppearance && trim.SkipBehavioralSummary && trim.StripGear &&
            knownNeeds.Count == 0 && (compressedMemories == null || compressedMemories.Count == 0);

        // CurrentActivity/CurrentMood share AffectsMoodOrActivity's gate with BehavioralSummary (the
        // latter is derived from the former plus recent events) — an NPC nobody touched this turn
        // collapses down to just Id/Name/roster flags plus whatever emergent initiative signal
        // (BehavioralTension/ActiveInitiatives/TurnIntent/CompressedMemories) live state still carries;
        // those come from current state, not AppliedChanges, so they're never gated here.
        return npc with
        {
            CurrentActivity = trim.SkipBehavioralSummary ? null : npc.CurrentActivity,
            CurrentMood = trim.SkipBehavioralSummary ? null : npc.CurrentMood,
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
            CompressedMemories = compressedMemories
        };
    }

    /// <summary>Shrinks an already delta-trimmed, fully-unchanged scene-present NPC entry down to
    /// id/name/roster-flags — the fields TurnResult.KnownCharacterIds' do-not-hallucinate check actually
    /// needs. The entry is never omitted entirely (that would drop a present character out of the
    /// known-entities list); only its content shrinks. See TurnCursor.SurfacedPresentNpcIdsByLocationId.</summary>
    private static NpcPresenceSummary StubPresence(NpcPresenceSummary npc) => npc with
    {
        CurrentActivity = null,
        CurrentMood = null,
        KnownNeeds = new Dictionary<string, float>(),
        NeedDescriptors = new Dictionary<string, string>(),
        BehavioralSummary = null,
        Notes = null,
        NotesTruncated = null,
        CurrentAppearance = null,
        VisualTags = null,
        DistinctiveFeatures = null,
        SystemStats = null,
        BehavioralTension = 0,
        ActiveInitiatives = null,
        RelevantMemories = null,
        EquippedItems = null,
        CarriedItems = null,
        TurnIntent = null,
        CompressedMemories = null
    };

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
        var explicitSceneCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                if (explicitlyRequested)
                {
                    explicitSceneCandidates.Add(id);
                }
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
            foreach (var id in ctx.InvolvedEntityIds)
            {
                AddCandidate(id, explicitlyRequested: false);
            }
        }

        if (npcCandidates.Count > 0)
        {
            // PCs travel via Party/PartyDelta, not Npcs[] — filter them out here (not just at
            // dedupe time) so they don't consume the NPC cap or trigger a spurious truncation.
            // Mirrors the !n.IsPc filters used for scene-embedded NPC presence elsewhere.
            var nonPcCandidates = new List<string>();
            foreach (var candidateId in npcCandidates)
            {
                var character = await ctx.Session.LoadAsync<Character>(candidateId);
                if (character?.IsPc != true)
                {
                    nonPcCandidates.Add(candidateId);
                }
            }
            npcCandidates = nonPcCandidates;
        }

        var truncatedIds = npcCandidates.Skip(NpcCap).Concat(sceneCandidates.Skip(SceneCap)).ToList();
        if (truncatedIds.Count > 0)
        {
            result.RefreshTruncatedIds = truncatedIds;
        }

        var scenesToFetch = sceneCandidates.Take(SceneCap).ToList();
        if (ctx.Mode == TurnMode.Delta)
        {
            // An auto-added candidate (not explicitly requested via ExtraLocationIds) only earns the
            // expensive full scene assembly (BuildSceneSummaryAsync: character-search + rumor + items +
            // combat + quests + faction + container-resolution queries) when something this turn actually
            // could have changed who/what is at that location. Plenty of change types reference a location
            // id incidentally without affecting presence — EventOccurred.LocationId/RelatedLocationIds
            // (routine event bookkeeping), RestChange.LocationId, FactionStateChange, QuestProgress, etc.
            // — and those alone shouldn't force a refetch every beat a character talks/rests/checks
            // something in a location nobody entered/left. The client already has the scene from the last
            // full reseed or a prior delta that changed it, same rationale as ApplyLocationDeltaTrim below.
            scenesToFetch = scenesToFetch
                .Where(id => explicitSceneCandidates.Contains(id)
                    || ctx.AppliedChanges.Any(c => AffectsLocationDetail(c, id) || AffectsScenePresence(c, id)))
                .ToList();
        }

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
                        var priorPresentIds = ctx.Cursor.SurfacedPresentNpcIdsByLocationId
                            .TryGetValue(locationId, out var priorIds)
                            ? new HashSet<string>(priorIds, StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        summary.PresentNPCs = summary.PresentNPCs
                            .Select(npc =>
                            {
                                var trimmed = ApplyDeltaTrim(ctx, npc, out var fullyUnchanged);
                                return fullyUnchanged && priorPresentIds.Contains(trimmed.Id)
                                    ? StubPresence(trimmed)
                                    : trimmed;
                            })
                            .ToList();

                        ctx.Cursor.SurfacedPresentNpcIdsByLocationId[locationId] =
                            summary.PresentNPCs.Select(n => n.Id).ToList();
                        if (ShouldStripUnchangedLocationDetail(ctx, locationId))
                        {
                            summary.Location = ApplyLocationDeltaTrim(summary.Location);
                        }
                        summary.LocalRumors = ApplyRumorDeltaTrim(ctx, summary.LocalRumors);
                        if (ctx.Mode == TurnMode.Delta)
                        {
                            // Campaign-wide legend text (what "stress"/"fatigue" mean) doesn't change
                            // turn to turn — the client already has it from the last full reseed or a
                            // prior delta scene.
                            summary.NeedDescriptorLegend = [];
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
                    var liveCharacter = await ctx.Session.LoadAsync<Character>(charId);
                    var liveNeeds = liveCharacter?.Needs?.ActiveNeeds ?? new Dictionary<string, float>();
                    var summary = await _repository.BuildNpcSummaryAsync(ctx.Session, charId, ctx.Campaign, BuildTrim(ctx, charId, liveNeeds));
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
                    // Only echo ambient/server-derived changes (ctx.AmbientChanges) — the caller already has
                    // every change object it just submitted in this same call's Changes[], so re-sending it
                    // is pure redundant bytes. Also exclude background need/attribute simulation ticks
                    // (hunger, tiredness, morale drift, climate readings) — they fire every turn for every
                    // scheduled NPC and are individually meaningless; MoodChange already surfaces the
                    // threshold crossings that matter narratively.
                    var memberChanges = ctx.AmbientChanges
                        .Where(c => _repository.ExtractInvolvedEntityIds(c).Contains(member.Id, StringComparer.OrdinalIgnoreCase))
                        .Where(c => !(c.IsEngineAuthored && c is NeedChange or AttributeChange))
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

    private async Task IncludeMemoriesOnlyAsync(TurnContext ctx)
    {
        var characterId = ctx.Request?.MemoriesOnlyCharacterId;
        if (string.IsNullOrEmpty(characterId))
        {
            return;
        }

        // Skip if fullDetailCharacterId already fetched this same NPC this turn — FullNpcContext
        // already carries Psychology.Memories, so a second fetch would be pure duplication.
        if (string.Equals(ctx.Request?.FullDetailCharacterId, characterId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var npc = await _repository.GetCharacterAsync(new CampaignSession(ctx.Session, ctx.Campaign), characterId);
            if (npc == null)
            {
                Warn(ctx, $"Memories-only fetch: '{characterId}' not found.");
                return;
            }

            ctx.Result.MemoriesOnly = new NpcMemoriesView
            {
                CharacterId = npc.Id,
                Name = npc.Name,
                Memories = npc.Psychology?.Memories.Values.ToList() ?? []
            };
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Memories-only fetch failed for '{characterId}': {ex.Message}", ex);
        }
    }

    private async Task StampPartyPresentLocationsAsync(TurnContext ctx)
    {
        try
        {
            var party = await ctx.Session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == ctx.Campaign && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            var locationIds = party
                .Where(c => !string.IsNullOrWhiteSpace(c.CurrentLocationId))
                .Select(c => c.CurrentLocationId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (locationIds.Count == 0)
            {
                return;
            }

            var time = await _repository.GetTimeAsync(new CampaignSession(ctx.Session, ctx.Campaign));
            var locations = await ctx.Session.LoadAsync<Location>(locationIds);
            foreach (var loc in locations.Values)
            {
                if (loc == null)
                {
                    continue;
                }

                loc.LastVisitedDay = time.TotalDaysElapsed;
                loc.LastUpdated = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Party visit stamp failed: {ex.Message}", ex);
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
            // Full detail here means full — bypass the Description/PointOfInterestDetails caps that
            // apply everywhere else, since this call is already bounded to one location per take_turn.
            var scene = await _repository.GetSceneAsync(new CampaignSession(ctx.Session, ctx.Campaign), locationId,
                markVisited: false, fullDescription: true, fullPointOfInterestDetails: true);
            if (scene != null)
            {
                // PCs ride along internally (recognition hints / faction-reputation lookups need
                // them), but they're not NPCs and their state already travels via Party/PartyDelta.
                scene.PresentNPCs = scene.PresentNPCs.Where(n => !n.IsPc).ToList();
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

        if (ctx.Mode == TurnMode.Full)
        {
            SeedNeedBaselinesOnFullReseed(ctx);
        }

        if (!string.IsNullOrEmpty(ctx.ReseedAdvisory))
        {
            AppendReminder(result, ctx.ReseedAdvisory);
        }

        if (!string.IsNullOrEmpty(ctx.NudgeAdvisory))
        {
            AppendReminder(result, ctx.NudgeAdvisory);
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

    /// <summary>On a Full response, every present NPC's KnownNeeds is exactly what the client just
    /// received — reset the cumulative-drift baseline (see ChangedNeedsKeys/TurnCursor.
    /// SurfacedNeedValuesByEntityId) to match, so a subsequent Delta call measures drift from what was
    /// actually sent, not from stale values left over from before the reseed.</summary>
    private static void SeedNeedBaselinesOnFullReseed(TurnContext ctx)
    {
        var eq = StringComparer.OrdinalIgnoreCase;

        void Seed(string entityId, Dictionary<string, float> knownNeeds)
        {
            ctx.Cursor.SurfacedNeedValuesByEntityId[entityId] = new Dictionary<string, float>(knownNeeds, eq);
        }

        foreach (var npc in ctx.Result.Npcs ?? [])
        {
            Seed(npc.CharacterId, npc.KnownNeeds);
        }

        foreach (var scene in ctx.Result.Scenes ?? [])
        {
            foreach (var presentNpc in scene.PresentNPCs)
            {
                Seed(presentNpc.Id, presentNpc.KnownNeeds);
            }
        }
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
        "math needed. Pass partyLocationId to get the same encounter/ambient-crowd checks travel/rest get for that " +
        "span (recommended whenever the span carries any real risk — resting somewhere unsafe, a dangerous overnight, " +
        "an unescorted journey); omit it for a guaranteed-safe skip with no interruption chance. Requires campaignName.")]
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
        int? hours = null,
        [Description("Location ID where the party is spending this span. When provided, the engine rolls the same encounter check rest/travel commits get for the elapsed time, and surfaces ambient-crowd/recently-departed pressure for that location. Omit for a guaranteed-safe skip.")]
        string? partyLocationId = null)
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
            var result = await _repository.AdvanceWorldAsync(session, days, resultingHour, effective, hours, partyLocationId);

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
