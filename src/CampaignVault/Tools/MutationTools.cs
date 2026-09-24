using System.ComponentModel;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Guidance;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase, IMcpServerTool
{
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;
    private readonly IGuidanceOrchestrator _guidanceOrchestrator;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;

    // Keyed per-campaign so commits in one campaign never throttle another. Bounded so a
    // long-running multi-campaign server can't grow this dictionary without limit: past the cap,
    // the least-recently-seen limiters are evicted (idle-first, then least-recent) and disposed.
    private const int RateLimiterCap = 256;
    private static readonly ConcurrentDictionary<string, RateLimiter> CommitRateLimiters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> RateLimiterLastSeenTicks = new(StringComparer.OrdinalIgnoreCase);

    internal static int RateLimiterCount => CommitRateLimiters.Count;

    internal static void ClearRateLimitersForTests()
    {
        foreach (var (key, limiter) in CommitRateLimiters)
        {
            if (CommitRateLimiters.TryRemove(key, out var removed))
            {
                RateLimiterLastSeenTicks.TryRemove(key, out _);
                removed.Dispose();
            }
        }
    }

    private static RateLimiter GetRateLimiter(string campaignName)
    {
        RateLimiterLastSeenTicks[campaignName] = DateTime.UtcNow.Ticks;

        if (CommitRateLimiters.Count > RateLimiterCap)
        {
            EvictStaleRateLimiters(campaignName);
        }

        return CommitRateLimiters.GetOrAdd(campaignName, key =>
        {
            RateLimiterLastSeenTicks[key] = DateTime.UtcNow.Ticks;
            return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 50,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                AutoReplenishment = true
            });
        });
    }

    private static void EvictStaleRateLimiters(string exemptCampaignName)
    {
        // Pass 1: evict fully-idle limiters (full bucket = no recent commits).
        foreach (var (key, limiter) in CommitRateLimiters)
        {
            if (CommitRateLimiters.Count <= RateLimiterCap)
            {
                break;
            }

            if (key.Equals(exemptCampaignName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (limiter.GetStatistics() is { CurrentAvailablePermits: >= 50 } &&
                CommitRateLimiters.TryRemove(key, out var removed))
            {
                RateLimiterLastSeenTicks.TryRemove(key, out _);
                removed.Dispose();
            }
        }

        // Pass 2 (user decision: bound incl. hot keys): still over cap, evict
        // least-recently-seen non-exempt limiters regardless of token balance.
        while (CommitRateLimiters.Count > RateLimiterCap)
        {
            string? oldestKey = null;
            var oldestTicks = long.MaxValue;
            foreach (var (key, seenTicks) in RateLimiterLastSeenTicks)
            {
                if (key.Equals(exemptCampaignName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!CommitRateLimiters.ContainsKey(key))
                {
                    RateLimiterLastSeenTicks.TryRemove(key, out _);
                    continue;
                }

                if (seenTicks < oldestTicks)
                {
                    oldestTicks = seenTicks;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                break;
            }

            if (CommitRateLimiters.TryRemove(oldestKey, out var removed))
            {
                RateLimiterLastSeenTicks.TryRemove(oldestKey, out _);
                removed.Dispose();
            }
            else
            {
                RateLimiterLastSeenTicks.TryRemove(oldestKey, out _);
            }
        }
    }

    public MutationTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator,
        IGuidanceOrchestrator guidanceOrchestrator,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        ILogger<MutationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
        _guidanceOrchestrator = guidanceOrchestrator;
        _behaviorSynthesizer = behaviorSynthesizer;
    }

    /// <summary>
    /// Mutable state threaded through the take_turn pipeline steps. Each step reads the request,
    /// enriches <see cref="Result"/>, and records non-fatal problems via <see cref="MutationTools.Warn"/>.
    /// </summary>
    private sealed class TurnContext(TakeTurnRequest? request, string campaign, IAsyncDocumentSession session)
    {
        public TakeTurnRequest? Request { get; } = request;

        /// <summary>Set when an HP-only fingerprint mismatch asks for the party block instead of a reseed.</summary>
        public bool PartyResyncRequested { get; set; }
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
        /// CommitResult.AmbientDeltas alone. Pre-P2-12, PartyDelta echoed only this subset in its Changes
        /// field; post-P2-12 it echoes the member's own applied movers too (see NeedsMoved), since a
        /// PartyDelta entry exists precisely to say "this member moved". Echoing still isn't a perfect
        /// receipt — a relative NeedChange delta echoed verbatim doesn't reflect server-side clamping —
        /// but NeedsMoved carries the live post-commit values, which do.</summary>
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

        /// <summary>Pre-commit itemId -> HolderId snapshot, taken before CommitChangesAsync applies any
        /// ItemTransfer/ItemUpdate in this batch. ItemTransfer names only the destination (ToHolderId —
        /// there is no FromHolderId field), and ItemUpdate names no holder at all, so without this the
        /// giver's gear (and any update-only holder's gear) would stay delta-stripped. Used by
        /// AffectsGearOrStats to keep both sides of a transfer (and update holders) fresh.</summary>
        public Dictionary<string, string?> ItemHolderBaselines { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        /// server-generated), so echoing them back added no information. (P2-12 exception: PartyDelta
        /// entries DO echo the member's own applied per-turn movers, via Changes + NeedsMoved, so a
        /// caller-submitted PC need push has a visible receipt.)</summary>
        public List<string> InvolvedEntityIds { get; set; } = [];

        /// <summary>Per-turn memo of ChangedNeedsKeys results by character ID. ChangedNeedsKeys advances
        /// the cursor drift baseline as a side effect, so a party companion evaluated for both Npcs[] and
        /// PartyDelta must reuse the first answer — a second evaluation would see the just-reset baseline
        /// and drop drift-only movers.</summary>
        public Dictionary<string, IReadOnlyCollection<string>> NeedsMoversByEntityId { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIFIED TURN TOOL: one call per narrative beat — commits changes[] atomically (any failure rolls back the whole batch) and returns fresh state for touched entities. Commit first, then narrate.

Pass changes[] + narrative, and/or a refresh param (includeParty, includeWorldState, extraCharacterIds, extraLocationIds, fullDetailCharacterId, memoriesOnlyCharacterId, fullDetailLocationId); a call with neither is rejected. Every change needs '$type'.

Echo the last partyFingerprint as clientPartyFingerprint; it tracks party HP + location, and a mismatch forces a resync. So set includeParty only when party HP/slots/gold/needs/AC/gear changed, and use fullDetailLocationId on the travel turn instead of a separate get_entity. ruleset_action auto-applies its damage/healing and grapple; don't also send hp for it. Utility-spell statuses are yours to commit in the same batch. Lasting item wear (scratches, stains, hidden compartments): item_update.upsertItemDetail. Delta-mode mechanics: lookup kind=help topic=take-turn-modes.")]
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
        if (hasChanges && rateLimiter.GetStatistics() is { CurrentAvailablePermits: <= 0 })
        {
            // Peek-only gate: validation failures (CommitChangesAsync below) never consume a
            // token, so "fix and resend FULL batch" retries don't wedge. The token is consumed
            // after a successful stage (see below) — TokenBucketRateLimiter has no refund API,
            // so consume-on-success is the only way to not charge client-fixable errors.
            return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        var commitTokenConsumed = false;

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
                    // Validation errors are client-fixable, not server overload: nothing
                    // consumed above (peek-only gate), so failed validation costs no budget.
                    return commitFailure;
                }

                // Stage succeeded: consume one token. TokenBucketRateLimiter has no refund
                // API, so consume-on-success (not acquire-then-refund) is what keeps failed
                // validation free while still throttling successful commits. The peek above is
                // racy under concurrent commits, so this acquire is the authoritative gate: a
                // failed result skips SaveChanges, discarding the staged batch. Charged once
                // across ExecuteAsync concurrency retries.
                if (!commitTokenConsumed)
                {
                    using var successLease = rateLimiter.AttemptAcquire();
                    if (!successLease.IsAcquired)
                    {
                        return new ToolResult<TurnResult>(false, Error: ToolErrors.RateLimitExceeded,
                            Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes.");
                    }

                    commitTokenConsumed = true;
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
            await CollectCharacterGuidanceAsync(ctx);

            return Finalize(ctx, rateLimiter);
        }, saveChanges: true);
    }

    /// <summary>
    /// Decides Full vs Delta for this call and persists the updated TurnCursor (via the already-open
    /// session — no extra SaveChangesAsync needed, ExecuteAsync's saveChanges:true covers it). Absence of
    /// a cursor document means take_turn has never been called for this campaign — naturally Full.
    /// P2-11: TurnsSinceReseed only advances on calls that committed mutations or returned substantial
    /// state (includeWorldState/includeParty/full-detail present); pure extraCharacterIds-only polls
    /// persist (saveChanges:true for pressure-cooldown state) but do not age the clock.
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
        else if (!isNewCursor && ClockAdvancingCall(ctx))
        {
            // New-cursor seed call: Full by definition ("never called before"), and it must not
            // pre-advance the clock — otherwise the NEXT call sees TurnsSinceReseed=1 from a seed
            // that was itself a Full, compressing interval bookkeeping by one.
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
    /// P2-11: whether this call ages the reseed clock. A call counts when it will commit mutations
    /// (Changes non-empty) or return substantial state (includeWorldState/includeParty/full-detail
    /// present); pure extraCharacterIds/extraLocationIds-only polls persist (saveChanges:true keeps
    /// pressure-cooldown state fresh) but do not advance TurnsSinceReseed.
    /// </summary>
    private static bool ClockAdvancingCall(TurnContext ctx)
    {
        if (ctx.Request?.Changes is { Length: > 0 })
        {
            return true;
        }

        return ctx.Request?.IncludeWorldState == true
            || ctx.Request?.IncludeParty == true
            || !string.IsNullOrEmpty(ctx.Request?.FullDetailCharacterId)
            || !string.IsNullOrEmpty(ctx.Request?.FullDetailLocationId)
            || !string.IsNullOrEmpty(ctx.Request?.MemoriesOnlyCharacterId);
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

        if (PartyFingerprint.SameLocations(clientValue, cursor.LastPartyFingerprint))
        {
            // B1: HP-only drift doesn't need a scene reseed; resend the party block so the model resyncs cheaply.
            _logger.LogInformation(
                "take_turn party fingerprint HP-only mismatch (campaign {Campaign}) — sending party instead of a full reseed", ctx.Campaign);
            ctx.PartyResyncRequested = true;
            return false;
        }

        _logger.LogWarning(
            "take_turn party fingerprint MISMATCH (campaign {Campaign}): expected '{Expected}', client echoed '{Actual}' — forcing full reseed",
            ctx.Campaign, cursor.LastPartyFingerprint, clientValue);
        return true;
    }

    /// <summary>
    /// P2-10 option (b): deliberately NARROW party fingerprint — "charId:hp/maxHp@locationId" per PC /
    /// companion, sorted by ID for determinism. It detects HP/location drift only; NPC/need/memory/
    /// rumor drift never trips it (that is covered by the 40-turn reseed + EntityIntegrityPressure +
    /// integrity pressure, not by this hash). Deliberately readable rather than an opaque hash — an LLM
    /// client can sanity-check it against its own narrative model directly, not just detect a dropped
    /// response. Mirrors IncludePartyAsync's WaitForNonStaleResults customization so a checksum computed
    /// immediately after a commit reflects what was just written, not a stale index read.
    /// An omitted client echo is never treated as a mismatch (see DetectPartyFingerprintDrift).
    /// </summary>
    private async Task<string> ComputePartyLocationHpFingerprintAsync(TurnContext ctx)
    {
        var party = await ctx.Session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
            .Where(c => c.CampaignName == ctx.Campaign && (c.IsPc || c.IsPartyCompanion))
            .ToListAsync();

        return PartyFingerprint.Compute(party);
    }

    /// <summary>
    /// Recomputes the party fingerprint as of the end of this call (post-commit if there was one), stores
    /// it on the cursor for next turn's drift check, and echoes it + WorldSequence in the result. Runs
    /// unconditionally (not just on mutation turns) so a pure-query call still keeps the drift check alive.
    /// </summary>
    private async Task RefreshPartyFingerprintAsync(TurnContext ctx)
    {
        var fingerprint = await ComputePartyLocationHpFingerprintAsync(ctx);
        ctx.Cursor.LastPartyFingerprint = fingerprint;
        ctx.Result.PartyFingerprint = fingerprint;
        ctx.Result.WorldSequence = ctx.Cursor.WorldSequence;
    }

    // P2-10(b): the old ComputePartyFingerprintAsync name was retired in favor of
    // ComputePartyLocationHpFingerprintAsync so the symbol says what the narrow hash covers.

    /// <summary>
    /// Collect guidance hints into the dedicated <see cref="TurnResult.GuidanceHints"/> field (own
    /// budget via CampaignConfig.MaxGuidanceHintsPerResponse/MaxGuidanceCharsPerResponse/
    /// GuidanceEnabled). PhysicalStateNudges stays physical/visual only per its V4Views contract.
    /// Runs when characters surfaced or changes were committed this turn; skips quiet pure-query turns.
    /// Covers every surfaced section: Npcs, Party, PartyDelta, scene PresentNPCs, FullNpcContext.
    /// </summary>
    private async Task CollectCharacterGuidanceAsync(TurnContext ctx)
    {
        if (ctx.Config?.GuidanceEnabled == false)
        {
            return;
        }

        // Build the surfaced-character id list from already-materialized sections (no extra
        // queries): Npcs, Party, PartyDelta, scene PresentNPCs (Scenes + FullScene), FullNpcContext.
        var surfacedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var npc in ctx.Result.Npcs ?? [])
        {
            if (!string.IsNullOrWhiteSpace(npc.CharacterId))
            {
                surfacedIds.Add(npc.CharacterId);
            }
        }

        foreach (var member in ctx.Result.Party ?? [])
        {
            if (!string.IsNullOrWhiteSpace(member.Id))
            {
                surfacedIds.Add(member.Id);
            }
        }

        foreach (var delta in ctx.Result.PartyDelta ?? [])
        {
            if (!string.IsNullOrWhiteSpace(delta.EntityId))
            {
                surfacedIds.Add(delta.EntityId);
            }
        }

        foreach (var scene in ctx.Result.Scenes ?? [])
        {
            foreach (var present in scene.PresentNPCs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(present.Id))
                {
                    surfacedIds.Add(present.Id);
                }
            }
        }

        if (ctx.Result.FullScene is not null)
        {
            foreach (var present in ctx.Result.FullScene.PresentNPCs ?? [])
            {
                if (!string.IsNullOrWhiteSpace(present.Id))
                {
                    surfacedIds.Add(present.Id);
                }
            }
        }

        var fullContextId = ctx.Result.FullNpcContext?.Character?.Id;
        if (!string.IsNullOrWhiteSpace(fullContextId))
        {
            surfacedIds.Add(fullContextId);
        }

        // A commit also counts: a delta turn that only enters a mode may surface nobody, and that is
        // exactly the edge plugin contributors key on.
        if (surfacedIds.Count == 0 && ctx.AppliedChanges.Count == 0)
        {
            return; // Quiet pure-query turn — no guidance cost.
        }

        try
        {
            var campaignTime = await _repository.GetTimeAsync(new CampaignSession(ctx.Session, ctx.Campaign));

            var pressureContext = new PressureContext(
                CampaignName: ctx.Campaign,
                Time: campaignTime,
                Config: ctx.Config,
                Session: ctx.Session,
                Scene: null, // Scene details not needed for character-scoped guidance
                PartyCharacterIds: surfacedIds.ToList().AsReadOnly(),
                PartyPresent: true,
                AppliedChanges: ctx.AppliedChanges);

            // Both scopes: Scene-only with a null Scene would never fire any contributor
            // (CombatStarted needs Scene.ActiveCombat; World contributors would never run),
            // leaving GuidanceHints permanently empty. World-scope hints are still
            // character-relevant via PartyCharacterIds; Scene contributors without a Scene
            // safely return empty.
            var hints = await _guidanceOrchestrator.CollectAsync(
                PressureScope.Both,
                pressureContext,
                ignoreLedger: false);

            if (hints.Count == 0)
            {
                return;
            }

            ctx.Result.GuidanceHints ??= [];
            foreach (var hint in hints)
            {
                var guidanceText = hint.Text;
                if (hint.Example != null)
                {
                    guidanceText += $" Example: {hint.Example}";
                }

                ctx.Result.GuidanceHints.Add(guidanceText);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to collect guidance hints");
        }
    }

    /// <summary>
    /// Snapshots (characterId,targetId) -> current relationship value for every RelationshipChange,
    /// characterId -> CurrentLocationId for every location-touching ActivityChange/TravelChange, and
    /// itemId -> HolderId for every ItemTransfer/ItemUpdate, in this batch, before CommitChangesAsync
    /// applies them — see TurnContext.RelationshipBaselines/LocationBaselines/ItemHolderBaselines for
    /// why these can't be reconstructed after the fact. Same session, so these loads
    /// are first-level-cache hits (CommitChangesAsync's handlers will load the same characters/items).
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

                case ItemTransfer it when !string.IsNullOrWhiteSpace(it.ItemId) && !ctx.ItemHolderBaselines.ContainsKey(it.ItemId):
                {
                    var transferItem = await ctx.Session.LoadAsync<Item>(it.ItemId);
                    ctx.ItemHolderBaselines[it.ItemId] = transferItem?.HolderId;
                    break;
                }

                case ItemUpdate iu when !string.IsNullOrWhiteSpace(iu.ItemId) && !ctx.ItemHolderBaselines.ContainsKey(iu.ItemId):
                {
                    var updatedItem = await ctx.Session.LoadAsync<Item>(iu.ItemId);
                    ctx.ItemHolderBaselines[iu.ItemId] = updatedItem?.HolderId;
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
            return Task.FromResult(new ToolResult<TurnResult>(false, Error: ToolErrors.InvalidArgument,
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
        // Plugin faults need no field of their own: each one is already a "PLUGIN FAULT ..." line in Summary.
        ctx.AppliedChanges = changes.Concat(commitResult.ReactionChanges).Concat(commitResult.AmbientDeltas).ToList();
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

        var triggerText = string.Join("\n",
            ctx.AppliedChanges.OfType<EventOccurred>().Select(e => e.Summary)
                .Concat(ctx.Request?.Narrative is { } n ? [n] : []));

        float[]? triggerVector = null;
        if (!string.IsNullOrWhiteSpace(triggerText))
        {
            triggerVector = await _repository.EmbedTriggerTextAsync(triggerText);
        }

        foreach (var npc in selected)
        {
            try
            {
                var enrichment = await _repository.EnrichNpcInitiativeAsync(
                    ctx.Session, npc, ctx.Campaign, "take_turn", includeTensionBreakdown: false,
                    triggerVector: triggerVector);

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

                // Only re-surface this NPC's MemoryHint when the topic+content differs from what the
                // client was already told (tracked on TurnCursor, no extra query) — a stable
                // high-salience memory shouldn't re-cost tokens every delta call it sits unresolved,
                // but an edit under the same topic is new information and must resurface.
                var hintKey = MemorySuppressionKey(topMemory);
                var alreadySurfaced = ctx.Cursor.SurfacedMemoryHintTopicsByEntityId.TryGetValue(npc.Id, out var lastHintKey)
                    && string.Equals(lastHintKey, hintKey, StringComparison.OrdinalIgnoreCase);
                if (alreadySurfaced)
                {
                    continue;
                }

                ctx.MemoryHintsByNpcId[npc.Id] =
                    $"{npc.Name} still has a high-salience memory '{topMemory.Topic}' — consider get_entity/recall_history if the conversation drifts toward it.";
                ctx.Cursor.SurfacedMemoryHintTopicsByEntityId[npc.Id] = hintKey;
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

    /// <summary>Suppression key for one memory's last-surfaced content: the topic plus a
    /// content hash (EmbeddingTextHash where the memory has been embedded, otherwise a cheap
    /// Topic+Details hash) — so an edit under the same topic re-surfaces while a stable topic
    /// stays suppressed. Mirrors ChangedNeedsKeys' baseline-comparison design: compare live state
    /// against what was last sent, and move the baseline forward only to what was actually sent.</summary>
    private static string MemorySuppressionKey(MemoryNode memory)
    {
        var contentHash = memory.EmbeddingTextHash ?? SemanticEnrichmentHash(memory.Topic, memory.Details);
        return $"{memory.Topic ?? string.Empty}#{contentHash}";
    }

    /// <summary>Hash fallback for memories that were never embedded (EmbeddingTextHash == null):
    /// same Topic+Details embedding-text shape SemanticEnrichmentHelper hashes post-embed, so the key
    /// stays stable across the pre/post-embed boundary rather than spuriously re-surfacing once.</summary>
    private static string SemanticEnrichmentHash(string? topic, string? details)
    {
        var text = $"{topic ?? string.Empty}\n{details ?? string.Empty}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Compresses an NPC's currently-relevant memories to topic+one-liner, dropping any
    /// memory whose topic+content hash was already sent to the client as of the last delta turn
    /// that surfaced it for this NPC — mirrors MemoryHintsByNpcId/
    /// SurfacedMemoryHintTopicsByEntityId's "don't re-cost tokens for a stable reading" gate, which
    /// CompressedMemories never had despite being the largest single field in a typical delta
    /// scene NPC. The cursor entry is replaced (not unioned) with the current key set every call,
    /// so a memory that drops out of relevance and later returns is treated as new again rather
    /// than permanently suppressed.</summary>
    private static List<CompressedMemory> CompressAndDedupeMemories(TurnContext ctx, string npcId, IReadOnlyList<MemoryNode> memories)
    {
        if (memories.Count == 0)
        {
            ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId.Remove(npcId);
            return [];
        }

        var alreadySurfaced = ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId.TryGetValue(npcId, out var priorKeys)
            ? new HashSet<string>(priorKeys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = memories
            .Where(m => !alreadySurfaced.Contains(MemorySuppressionKey(m)))
            .Select(m => new CompressedMemory(m.Topic, Truncate(m.Details ?? string.Empty, 140)))
            .ToList();

        ctx.Cursor.SurfacedCompressedMemoryTopicsByEntityId[npcId] = memories.Select(MemorySuppressionKey).ToList();
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
    private bool AffectsGearOrStats(TurnContext ctx, WorldChange change, string characterId)
    {
        var eq = StringComparer.OrdinalIgnoreCase;
        return change switch
        {
            // ItemTransfer names only the destination — the giver is recovered from the pre-commit
            // holder snapshot (see TurnContext.ItemHolderBaselines); ItemTransfer has no FromHolderId.
            ItemTransfer it => eq.Equals(it.ToHolderId, characterId) ||
                (!string.IsNullOrWhiteSpace(it.ItemId) && ctx.ItemHolderBaselines.TryGetValue(it.ItemId, out var beforeHolder) &&
                 eq.Equals(beforeHolder, characterId)),
            // ItemUpdate names no holder at all — match via the pre-commit holder snapshot.
            ItemUpdate iu => !string.IsNullOrWhiteSpace(iu.ItemId) && ctx.ItemHolderBaselines.TryGetValue(iu.ItemId, out var holder) &&
                eq.Equals(holder, characterId),
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
        ctx.Mode == TurnMode.Delta && !ctx.AppliedChanges.Any(c => AffectsGearOrStats(ctx, c, characterId));

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
            // Injury beats plausibly change appearance (gash, limp, pallor) — resend it so a
            // narrated wound doesn't desync from the engine's stored CurrentAppearance.
            HpChange hp => eq.Equals(hp.CharacterId, characterId),
            StatusChange sc => eq.Equals(sc.CharacterId, characterId),
            StatusRemove sr => eq.Equals(sr.CharacterId, characterId),
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

        if (ctx.NeedsMoversByEntityId.TryGetValue(characterId, out var memoized))
        {
            return memoized;
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

        ctx.NeedsMoversByEntityId[characterId] = movers;
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
        // id/name/roster-flags stub (see TurnCursor.SurfacedPresentNpcIdsByLocationId). Tension is
        // deliberately left OUT of this gate (it reflects live state, not AppliedChanges): a stale
        // non-zero tension must not keep an otherwise-unchanged row "interesting" — the stub path
        // below nulls it to "unknown" (see StubPresence) instead of echoing a cached reading.
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
            Stats = trim.StripGear ? null : npc.Stats,
            EquippedItems = trim.StripGear ? null : npc.EquippedItems,
            CarriedItems = trim.StripGear ? null : npc.CarriedItems,
            RelevantMemories = npc.RelevantMemories is { Count: > 0 } ? [] : npc.RelevantMemories,
            CompressedMemories = compressedMemories
        };
    }

    /// <summary>Shrinks an already delta-trimmed, fully-unchanged scene-present NPC entry down to
    /// id/name/roster-flags — the fields TurnResult.KnownCharacterIds' do-not-hallucinate check actually
    /// needs. The entry is never omitted entirely (that would drop a present character out of the
    /// known-entities list); only its content shrinks. BehavioralTension is nulled (unknown) rather
    /// than zeroed: the stub means "no fresh reading this turn", and McpResponseCleaner strips nulls
    /// while keeping a genuine measured 0 — so a stubbed row carries no key while measured-calm keeps
    /// 0. See TurnCursor.SurfacedPresentNpcIdsByLocationId.</summary>
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
        Stats = null,
        BehavioralTension = null,
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

        // Departure-side scenes: AffectsScenePresence only matches the destination, so without this
        // the source scene's roster goes stale (still lists the departed) until the next full reseed.
        // Only genuine transitions (LocationChanged against the pre-commit LocationBaselines) earn the
        // extra fetch; unknown origins (before == null) are skipped. Delta-only — Full mode fetches
        // every candidate unfiltered, so this stays scoped to the delta gate below.
        var departureSceneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx.Mode == TurnMode.Delta)
        {
            foreach (var change in ctx.AppliedChanges)
            {
                string? moverId = null;
                string? newLocationId = null;
                if (change is TravelChange tc)
                {
                    moverId = tc.CharacterId;
                    newLocationId = tc.DestinationLocationId;
                }
                else if (change is ActivityChange ac && ac.UpdateLocation)
                {
                    moverId = ac.CharacterId;
                    newLocationId = ac.NewLocationId;
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(moverId) || !LocationChanged(ctx, moverId!, newLocationId))
                {
                    continue;
                }

                if (!ctx.LocationBaselines.TryGetValue(moverId!, out var before) || string.IsNullOrWhiteSpace(before))
                {
                    continue;
                }

                if (departureSceneIds.Add(before!))
                {
                    AddCandidate(before!, explicitlyRequested: false);
                }
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
                    || departureSceneIds.Contains(id)
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
        if (ctx.Request?.IncludeParty != true && !ctx.PartyResyncRequested)
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
                // P2-12 OMISSION CONTRACT: a quiet turn surfaces no PartyDelta at all — absence means
                // "no party change worth surfacing", not "no party". A member surfaces when it has
                // ambient (server-derived) non-need changes, need movers past the significance +
                // cumulative-drift gates (same ChangedNeedsKeys machinery as Npcs[], reused not
                // duplicated), or initiative/memory enrichment.
                var deltas = new List<EntityChangeDelta>();
                foreach (var member in party)
                {
                    // Echo the member's own applied Changes[] objects here (unlike the pre-P2-12 rule):
                    // a PartyDelta entry exists precisely to say "this member moved", and the caller-side
                    // suppression rationale (already-has-it) is outweighed by symmetry with Npcs[] (whose
                    // KnownNeeds surfaces caller-submitted per-turn movers via ChangedNeedsKeys) — without
                    // this, a caller-submitted PC hunger push would trip the mover gate yet surface no
                    // receipt of what landed. Still exclude background engine-authored need/attribute
                    // simulation ticks (hunger, tiredness, morale drift, climate readings) — they fire
                    // every turn for every scheduled NPC and are individually meaningless; the
                    // ChangedNeedsKeys gate below surfaces the threshold crossings instead.
                    var memberChanges = ctx.AppliedChanges
                        .Where(c => _repository.ExtractInvolvedEntityIds(c).Contains(member.Id, StringComparer.OrdinalIgnoreCase))
                        .Where(c => !(c.IsEngineAuthored && c is NeedChange or AttributeChange))
                        .ToList();
                    var liveNeeds = member.Needs?.ActiveNeeds
                        ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    // ChangedNeedsKeys is Delta-only here by construction (this branch runs only on
                    // Delta) and updates the cumulative-drift baseline for surfaced keys as a side
                    // effect, exactly like the Npcs[] path via BuildTrim/ApplyDeltaTrim.
                    var movedNeedKeys = ChangedNeedsKeys(ctx, member.Id, liveNeeds)
                        ?? new List<string>();
                    var hasInitiative = ctx.InitiativeByNpcId.TryGetValue(member.Id, out var initiative);
                    var hasMemoryHint = ctx.MemoryHintsByNpcId.TryGetValue(member.Id, out var memoryHint);

                    if (memberChanges.Count == 0 && movedNeedKeys.Count == 0 && !hasInitiative && !hasMemoryHint)
                    {
                        continue;
                    }

                    deltas.Add(new EntityChangeDelta
                    {
                        EntityId = member.Id,
                        Name = member.Name,
                        Changes = memberChanges,
                        NeedsMoved = movedNeedKeys.Count > 0
                            ? liveNeeds
                                .Where(kv => movedNeedKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                            : null,
                        Initiative = initiative,
                        MemoryHint = memoryHint
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

                // P2-13: a Full carries complete Time + pressure — it also (re)baselines the delta
                // suppression markers, same as SeedNeedBaselinesOnFullReseed does for needs.
                ctx.Cursor.LastSurfacedDay = worldState.Time.Day;
                ctx.Cursor.LastSurfacedMonth = worldState.Time.Month;
                ctx.Cursor.LastSurfacedYear = worldState.Time.Year;
                ctx.Cursor.LastSurfacedTimeOfDay = TimeOfDayBucket(worldState.Time.Hour);
                // Baseline the pressure marker on the rich items (grouping+entity+signature — stable
                // across the ToDisplayStrings batching that collapses N items into one rendered line).
                // An empty set is still a reading the next identical poll suppresses against.
                ctx.Cursor.LastSurfacedPressureKeys = (worldState.WorldPressureItems?.ToList() ?? [])
                    .Select(p => PressureItemKey(p))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                // P2-13 pure suppression: Time sends only when day/time-of-day shifted since last
                // surfaced; WorldPressure sends only when the evaluated set differs from the
                // last-surfaced set (grouping-key keyed — new/changed always sends; only
                // identical-to-last drops, flagged via PressureUnchanged:true). Rumor/quest/faction
                // change-filtering is unchanged (AppliedChanges-gated).
                var newEvents = new List<string>();
                newEvents.AddRange(ctx.AmbientNarrativeSummaries);

                var timeShifted = IsTimeShiftedSinceSurfaced(ctx, worldState.Time);
                var (pressureChanged, pressureToSend) = DiffWorldPressureSinceSurfaced(ctx, worldState);

                ctx.Result.WorldStateDelta = new WorldStateDeltaView
                {
                    Time = timeShifted ? worldState.Time : null,
                    PressureUnchanged = !pressureChanged,
                    WorldPressure = pressureToSend,
                    RumorChanges = ctx.AppliedChanges.OfType<RumorEvolves>().ToList(),
                    QuestChanges = ctx.AppliedChanges.OfType<QuestProgress>().ToList(),
                    FactionReputationChanges = ctx.AppliedChanges.OfType<FactionReputationChange>().ToList(),
                    FactionStateChanges = ctx.AppliedChanges.OfType<FactionStateChange>().ToList(),
                    NewEvents = newEvents.Count > 0 ? newEvents : null
                };

                if (timeShifted)
                {
                    ctx.Cursor.LastSurfacedDay = worldState.Time.Day;
                    ctx.Cursor.LastSurfacedMonth = worldState.Time.Month;
                    ctx.Cursor.LastSurfacedYear = worldState.Time.Year;
                    ctx.Cursor.LastSurfacedTimeOfDay = TimeOfDayBucket(worldState.Time.Hour);
                }

                if (pressureChanged)
                {
                    ctx.Cursor.LastSurfacedPressureKeys = (worldState.WorldPressureItems?.ToList() ?? [])
                        .Select(p => PressureItemKey(p))
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Warn(ctx, $"World-state section failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// P2-13: Time is "shifted" when the calendar day/month/year moved or the coarse time-of-day bucket
    /// (CampaignTime.GetTimeOfDayName) changed since the last delta that actually carried Time. Tracked
    /// on the TurnCursor like the need baselines, so hour-level ticks inside one bucket stay suppressed.
    /// First-ever delta (no surfaced marker yet) always sends — absence of a baseline is not sameness.
    /// </summary>
    private static bool IsTimeShiftedSinceSurfaced(TurnContext ctx, CampaignTimeView time)
    {
        if (ctx.Cursor.LastSurfacedTimeOfDay is null)
        {
            return true;
        }

        return ctx.Cursor.LastSurfacedDay != time.Day
            || ctx.Cursor.LastSurfacedMonth != time.Month
            || ctx.Cursor.LastSurfacedYear != time.Year
            || !string.Equals(ctx.Cursor.LastSurfacedTimeOfDay, TimeOfDayBucket(time.Hour),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// P2-13: coarse time-of-day bucket mirrored from CampaignTime.GetTimeOfDayName (kept local so the
    /// delta gate doesn't depend on allocating a FormattedDate string parse per poll).
    /// </summary>
    private static string TimeOfDayBucket(int hour) =>
        hour switch
        {
            >= 0 and < 6 => "Night",
            >= 6 and < 9 => "Dawn",
            >= 9 and < 12 => "Morning",
            >= 12 and < 15 => "Noon",
            >= 15 and < 18 => "Afternoon",
            >= 18 and < 21 => "Evening",
            >= 21 and < 24 => "Dusk",
            _ => "Night"
        };

    /// <summary>
    /// P2-13 pure suppression for WorldPressure: compares the currently evaluated rich pressure items
    /// against the last-surfaced set, keyed on grouping+entity+severity+content signature (see
    /// PressureItemKey; digit-normalized except quest countdowns) —
    /// the same identity the pressure pipeline itself uses (PressureOrchestrator merge key +
    /// PressureHelpers.ComputeContentSignature, so numeric-only text changes like 66%-&gt;67% do not
    /// count as changed, and ToDisplayStrings batching that collapses N items into one rendered line
    /// does not look like churn). Order-insensitive; identical-to-last yields (false, []) with the
    /// caller flagging PressureUnchanged:true; any difference yields (true, full current set).
    /// First-ever delta (no surfaced set yet) always sends — suppression must be conservative per the
    /// skill-docs gotcha (omit includeWorldState → never see resolution).
    /// </summary>
    private static (bool Changed, List<string> ToSend) DiffWorldPressureSinceSurfaced(
        TurnContext ctx, WorldStateView worldState)
    {
        var current = (worldState.WorldPressureItems?.ToList() ?? [])
            .Select(p => PressureItemKey(p))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var prior = (ctx.Cursor.LastSurfacedPressureKeys ?? [])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // No baseline yet (pre-P2-13 cursor, or a cursor from a campaign whose Full never
        // ran through this code): always send — absence of a baseline is not sameness. Note a
        // post-P2-13 Full always writes the marker (even for an empty set), so this branch only
        // fires for legacy cursors, where one extra send self-heals the baseline going forward.
        if (ctx.Cursor.LastSurfacedPressureKeys is null)
        {
            return (true, worldState.WorldPressure?.ToList() ?? []);
        }

        if (current.SequenceEqual(prior, StringComparer.Ordinal))
        {
            return (false, []);
        }

        return (true, worldState.WorldPressure?.ToList() ?? []);
    }

    internal static string PressureItemKey(WorldPressureItem item)
    {
        // Digit-normalized so per-turn ticks (hunger %, felt temperature, HP) don't churn the delta,
        // except quest countdowns: "3 days" -> "1 days" is a discrete step the client must see.
        // Severity is part of the identity so an escalation always re-sends.
        var normalizeDigits = item.GroupingKey != QuestDeadlinePressureContributor.ApproachingDeadlineGroupingKey;
        return $"{item.GroupingKey}:{item.EntityId}:{item.Severity}:{PressureHelpers.ComputeContentSignature(item.Text, normalizeDigits)}";
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
            var locationIds = await SimulationQueryHelper.QueryPartyLocationIdsAsync(ctx.Session, ctx.Campaign);

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
                // Parity with get_entity's scene view, so arriving via travel + fullDetailLocationId
                // doesn't need a follow-up get_entity for location ENGINE WARNINGs or plot threads.
                // Runs before the PC strip below: scene contributors (e.g. location integrity) need
                // PCs in the roster, same as get_entity.
                var time = await _repository.GetTimeAsync(new CampaignSession(ctx.Session, ctx.Campaign));
                var config = ctx.Config ?? await _repository.GetCampaignConfigAsync(new CampaignSession(ctx.Session, ctx.Campaign));
                var scenePressure = await _pressureOrchestrator.CollectAndCapAsync(PressureScope.Scene, new PressureContext(
                    ctx.Campaign, time, config, ctx.Session, Scene: scene, RequestedLocationId: locationId, PartyPresent: true));
                if (scenePressure.Count > 0)
                {
                    scene.ScenePressure = PressureManager.ToDisplayStrings(scenePressure).ToList();
                }

                // PCs ride along internally (recognition hints / faction-reputation lookups need
                // them), but they're not NPCs and their state already travels via Party/PartyDelta.
                scene.PresentNPCs = scene.PresentNPCs.Where(n => !n.IsPc).ToList();
                var threads = await _repository.GetPlotThreadsReferencingEntityAsync(ctx.Session, locationId, ctx.Campaign);
                scene.AssociatedPlotThreads = threads
                    .Select(t => new PlotThreadMinimal(t.Id, t.Title, t.State, t.TensionLevel))
                    .ToList();
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

    /// <summary>On a Full response, every present NPC's KnownNeeds (and every party member's full needs)
    /// is exactly what the client just received — reset the cumulative-drift baseline (see
    /// ChangedNeedsKeys/TurnCursor.SurfacedNeedValuesByEntityId) to match, so a subsequent Delta call
    /// measures drift from what was actually sent, not from stale values left over from before the
    /// reseed. P2-12: seeds Full Party[] members alongside Npcs[]/Scenes[] so PC drift baselines
    /// initialize like NPC ones.</summary>
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

        // P2-12: seed party baselines too — Full Party[] carries the whole Needs dict (via
        // CharacterDetailView); without this, PC drift baselines never initialize and slow hunger
        // accumulation stays invisible forever. (Delta PartyDelta[].NeedsMoved needs no seeding here:
        // ChangedNeedsKeys already updated the cursor baseline for surfaced keys as a side effect.)
        foreach (var member in ctx.Result.Party ?? [])
        {
            Seed(member.Id, new Dictionary<string, float>(member.Character.Needs?.ActiveNeeds
                ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase), eq));
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

        NpcPresenceSummary MergeInitiative(NpcPresenceSummary n, NpcSummaryView dropped)
        {
            if (!n.Id.Equals(dropped.CharacterId, StringComparison.OrdinalIgnoreCase)
                || dropped.Initiative == null)
            {
                return n;
            }

            var droppedInitiatives = dropped.Initiative.ActiveInitiatives ?? [];
            var sceneInitiatives = n.ActiveInitiatives ?? [];
            var droppedTension = dropped.Initiative.BehavioralTension;

            // The winner's enrichment (Npcs[] side) is strictly richer — full candidate list plus
            // memories computed at real cost this turn — so when it carries initiative signal it wins
            // over the scene-presence copy, which may itself be a stubbed/unknown row (null tension =
            // "no signal", not "calm") from StubPresence. An absent tension on the scene side never
            // blocks the merge the way a fabricated zero once did.
            var sceneHasSignal = (n.BehavioralTension ?? 0) != 0
                || sceneInitiatives.Count > 0
                || n.TurnIntent != null;
            var droppedHasSignal = droppedTension != 0
                || droppedInitiatives.Count > 0
                || dropped.Initiative.TurnIntent != null;

            if (!droppedHasSignal)
            {
                return n;
            }

            if (!sceneHasSignal)
            {
                return n with
                {
                    BehavioralTension = droppedTension,
                    ActiveInitiatives = dropped.Initiative.ActiveInitiatives,
                    RelevantMemories = dropped.Initiative.RelevantMemories,
                    TurnIntent = dropped.Initiative.TurnIntent
                };
            }

            // Both sides carry signal (the P1-9 loss case): prefer the winner — max-merge tension so
            // the hotter reading survives, concat initiatives (winner first) so neither side's
            // candidates are silently discarded, and let the winner's explicit TurnIntent win.
            var mergedInitiatives = droppedInitiatives
                .Concat(sceneInitiatives.Where(s => !droppedInitiatives.Any(d =>
                    string.Equals(d.Key, s.Key, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            return n with
            {
                BehavioralTension = Math.Max(n.BehavioralTension ?? 0, droppedTension),
                ActiveInitiatives = mergedInitiatives,
                RelevantMemories = dropped.Initiative.RelevantMemories ?? n.RelevantMemories,
                TurnIntent = dropped.Initiative.TurnIntent ?? n.TurnIntent
            };
        }

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
        "Skip uneventful downtime and run world simulation (rumors, factions, plots). Use hours (e.g. 8) for a night " +
        "or part of a day, or days + resultingHour for longer. Pass partyLocationId to roll encounter checks for the " +
        "span; omit it only for a risk-free skip.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Summary of the rest, travel, or downtime activity.")]
        string narrative,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Whole days to skip (use with resultingHour; not with hours).")]
        int days = 0,
        [Description("Resulting hour 0-23 when using days.")]
        int? resultingHour = null,
        [Description("Hours to fast-forward from now (e.g. 8 for a night's sleep).")]
        int? hours = null,
        [Description("Where the party spends the span; enables encounter checks. Omit for a safe skip.")]
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
                turnCursor = new TurnCursor { Id = _keys.StateTurnCursor(effective), CampaignName = effective, ForcedFullReseedPending = true };
                await session.StoreAsync(turnCursor, _keys.StateTurnCursor(effective));
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

            // F6: the skip heals HP in this session; load tracked instances (not an index query) so the
            // fingerprint reflects the unsaved changes, and store it so the next take_turn sees no false drift.
            var partyDocs = await session.LoadAsync<Character>(partyIds);
            var fingerprint = PartyFingerprint.Compute(partyDocs.Values.Where(c => c != null));
            result.PartyFingerprint = fingerprint;
            turnCursor.LastPartyFingerprint = fingerprint;

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
