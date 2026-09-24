using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Per-campaign singleton tracking take_turn's Full/Delta reseed cadence. Document ID should be
/// provided by CampaignDocumentKeys.StateTurnCursor(campaignName) (e.g. "campaigns/{name}/state/turn-cursor").
/// Absence of this document means "no take_turn call has happened yet" — the first call is naturally Full.
/// </summary>
public class TurnCursor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("campaignName")]
    public string? CampaignName { get; set; }

    /// <summary>Number of substantial take_turn calls since the last Full response. Reset to 0 on Full.
    /// A call counts only when it committed mutations or returned substantial state (includeWorldState /
    /// includeParty / full-detail present) — pure extraCharacterIds/extraLocationIds-only polls persist
    /// (saveChanges:true for pressure-cooldown state) but do not age this clock (P2-11).</summary>
    [JsonPropertyName("turnsSinceReseed")]
    public int TurnsSinceReseed { get; set; }

    [JsonPropertyName("lastFullReseedUtc")]
    public DateTime? LastFullReseedUtc { get; set; }

    /// <summary>
    /// Set by advance_world (which can run simulation ticks outside the take_turn pipeline) to force
    /// the next take_turn call to Full regardless of TurnsSinceReseed, so drift from a skip isn't missed.
    /// </summary>
    [JsonPropertyName("forcedFullReseedPending")]
    public bool ForcedFullReseedPending { get; set; }

    /// <summary>
    /// Consecutive take_turn calls where the client set ForceFullReseed=true while TurnsSinceReseed was
    /// still low (i.e., forcing again shortly after already being reseeded). Reset to 0 whenever
    /// ForceFullReseed isn't set. Used only to surface an advisory hint — never suppresses the client's
    /// explicit request.
    /// </summary>
    [JsonPropertyName("consecutiveClientForcedReseeds")]
    public int ConsecutiveClientForcedReseeds { get; set; }

    /// <summary>Monotonically increasing, bumped once per committed take_turn mutation. Never reset.
    /// Informational for now (logging/debugging state sync) — not yet used for gap-based reseed decisions.</summary>
    [JsonPropertyName("worldSequence")]
    public long WorldSequence { get; set; }

    /// <summary>P2-10(b): deliberately narrow party HP/location fingerprint as of the end of the last
    /// take_turn call ("charId:hp/maxHp@locationId", sorted by ID). Compared against the client's echoed
    /// TakeTurnRequest.ClientPartyFingerprint on the next call to detect HP/location drift (a missed or
    /// misread delta) independent of the periodic reseed cadence. Does NOT cover NPC/need/memory/rumor
    /// drift — that is covered only by the periodic reseed + integrity pressure.</summary>
    [JsonPropertyName("lastPartyFingerprint")]
    public string? LastPartyFingerprint { get; set; }

    /// <summary>Topic + content hash of the high-salience-memory nudge (NpcSummaryView/
    /// EntityChangeDelta.MemoryHint) last surfaced to the client per entity ID, e.g.
    /// "Secret#AB12..". take_turn skips re-sending a MemoryHint whose key matches what's already
    /// here — the client already has the nudge — and updates the entry whenever a new/different
    /// topic OR newly-edited details under the same topic are surfaced. Entities are only added
    /// here once their top memory first clears the salience bar, so this stays small relative to
    /// campaign NPC count.</summary>
    [JsonPropertyName("surfacedMemoryHintTopicsByEntityId")]
    public Dictionary<string, string> SurfacedMemoryHintTopicsByEntityId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Topic + content-hash keys ("Topic#HASH", see MutationTools.MemorySuppressionKey) of
    /// CompressedMemory entries (NpcPresenceSummary/NpcInitiativeEnrichment) already sent to the
    /// client per entity ID, as of the last delta turn that surfaced them — same "don't
    /// re-cost tokens for a stable reading" gate as <see cref="SurfacedMemoryHintTopicsByEntityId"/>,
    /// but per-entity a full key set rather than a single key since an NPC can carry several relevant
    /// memories at once. Replaced (not unioned) with each call's current key set, so a memory that
    /// drops out of relevance and later returns is treated as new again.</summary>
    [JsonPropertyName("surfacedCompressedMemoryTopicsByEntityId")]
    public Dictionary<string, List<string>> SurfacedCompressedMemoryTopicsByEntityId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last need value actually surfaced to the client per entity ID, per need key — the
    /// baseline cumulative drift is measured against, not any single turn's NeedChange alone. A need
    /// clears CampaignConfig.NeedsChangeSignificanceThreshold when its live value has drifted far
    /// enough from THIS baseline since it was last surfaced, even if no single turn's delta was enough
    /// on its own. Seeded for every NPC's AND every party member's full needs on a Full reseed (P2-12),
    /// and updated only for needs actually included in a Delta response, so an omitted need keeps
    /// accumulating drift against its last-sent baseline.</summary>
    [JsonPropertyName("surfacedNeedValuesByEntityId")]
    public Dictionary<string, Dictionary<string, float>> SurfacedNeedValuesByEntityId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>NPC IDs present in a location the last time that scene was actually included in a
    /// take_turn response (Full or Delta), keyed by location ID. On a Delta refetch, an NPC ID already
    /// in this set whose presence/fields are both unchanged this turn (see MutationTools.ApplyDeltaTrim's
    /// fullyUnchanged out-param) shrinks to an id/name/roster-flags stub instead of resending its full
    /// trimmed entry — it must still appear (the roster is the do-not-hallucinate list of who is present),
    /// just smaller. Replaced (not unioned) with each scene fetch's current present-NPC set, so a
    /// departed-then-returned NPC or a newly arrived one is never mistaken for "already surfaced."</summary>
    [JsonPropertyName("surfacedPresentNpcIdsByLocationId")]
    public Dictionary<string, List<string>> SurfacedPresentNpcIdsByLocationId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rolling window (oldest first, capped at <see cref="CampaignConfig.ForcedReseedThrashWindow"/> entries)
    /// of whether each of the last few take_turn calls had ForceFullReseed=true. Unlike
    /// ConsecutiveClientForcedReseeds (which only catches back-to-back forces), this catches a client
    /// forcing full reseeds too often even when it alternates with delta calls in between — e.g.
    /// full,delta,full,delta,full,full, a pattern seen from some models thrashing on when to trust delta
    /// mode. Used only to surface a self-correcting advisory; never suppresses an explicit client request.
    /// </summary>
    [JsonPropertyName("recentClientForcedFlags")]
    public List<bool> RecentClientForcedFlags { get; set; } = [];

    /// <summary>NPC IDs with a pending NpcInitiativeNudge (see MutationTools.ApplyInitiativeNudges),
    /// awaiting consumption by the next eligible initiative selection (MutationTools.
    /// SelectAndEnrichInitiativeAsync) — a nudged NPC wins that slot outright, bypassing the normal
    /// need/momentum scorer and cooldown, since the nudge represents a specific reactive beat rather
    /// than routine rotation. Decays (TurnsRemaining counts down every take_turn call, entry removed at
    /// 0) if never consumed, regardless of whether the NPC ever becomes a candidate in the meantime.</summary>
    [JsonPropertyName("pendingInitiativeNudgesByEntityId")]
    public Dictionary<string, PendingInitiativeNudge> PendingInitiativeNudgesByEntityId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many nudges in a row an NPC has received without any of them being consumed by a
    /// selection in between (1 for a fresh nudge with none pending, incremented on each nudge issued
    /// while a previous one is still pending). Reset to 1 whenever a fresh nudge starts the streak, and
    /// implicitly cleared to 0 on consumption. Reaching CampaignConfig.InitiativeNudgeRepeatThreshold
    /// appends an advisory reminder asking the DM to let a reaction play out before layering on another —
    /// see MutationTools.ApplyInitiativeNudges.</summary>
    [JsonPropertyName("consecutiveUnconsumedNudgesByEntityId")]
    public Dictionary<string, int> ConsecutiveUnconsumedNudgesByEntityId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>P2-13: day/month/year + coarse time-of-day bucket (CampaignTime.GetTimeOfDayName) of the
    /// Time last actually carried in a WorldStateDelta. Null = no delta has carried Time yet, so the next
    /// one always sends. Tracked like the need baselines — same "last actually surfaced" semantics.</summary>
    [JsonPropertyName("lastSurfacedDay")]
    public int? LastSurfacedDay { get; set; }

    [JsonPropertyName("lastSurfacedMonth")]
    public int? LastSurfacedMonth { get; set; }

    [JsonPropertyName("lastSurfacedYear")]
    public int? LastSurfacedYear { get; set; }

    [JsonPropertyName("lastSurfacedTimeOfDay")]
    public string? LastSurfacedTimeOfDay { get; set; }

    /// <summary>P2-13: display-string set (ordered, grouping-key keyed via the display text) of the
    /// WorldPressure last actually carried in a WorldStateDelta. Null = no delta has carried pressure
    /// yet, so the next one always sends — suppression must be conservative (new/changed always sends;
    /// only identical-to-last may drop).</summary>
    [JsonPropertyName("lastSurfacedPressureKeys")]
    public List<string>? LastSurfacedPressureKeys { get; set; }

    /// <summary>T6 delivery ledger: NPC ID → NpcCard.StableHash() of the card last sent this session. A
    /// card is resent only when its stable fields change. Cleared with the rest of the ledger on every
    /// Full response (start_session, forceFullReseed, the reseed interval, drift), since the client may
    /// have lost it.</summary>
    [JsonPropertyName("deliveredCardHashes")]
    public Dictionary<string, string> DeliveredCardHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>T6 delivery ledger: keys of context lines (memories pushed, tier crossings, gold, ...) sent this session.</summary>
    [JsonPropertyName("deliveredContextKeys")]
    public List<string> DeliveredContextKeys { get; set; } = [];

    /// <summary>T6 delivery ledger: locations whose description (and DM-only secrets) were sent this session.</summary>
    [JsonPropertyName("briefedLocationIds")]
    public List<string> BriefedLocationIds { get; set; } = [];

    /// <summary>Set by start_session: a new conversation has none of the once-per-session deliveries, so the
    /// next take_turn clears the ledger. (A Full forced by advance_world is not a context loss and keeps it.)</summary>
    [JsonPropertyName("ledgerResetPending")]
    public bool LedgerResetPending { get; set; }

    /// <summary>Forget what this session has been sent: the next cards, context lines and location
    /// descriptions go out again.</summary>
    public void ClearDeliveryLedger()
    {
        DeliveredCardHashes.Clear();
        DeliveredContextKeys.Clear();
        BriefedLocationIds.Clear();
    }
}

/// <summary>A pending, not-yet-consumed NpcInitiativeNudge — see
/// TurnCursor.PendingInitiativeNudgesByEntityId.</summary>
public class PendingInitiativeNudge
{
    [JsonPropertyName("intensity")]
    public float Intensity { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Counts down by 1 every take_turn call; the entry is removed once this reaches 0 without
    /// being consumed by a selection.</summary>
    [JsonPropertyName("turnsRemaining")]
    public int TurnsRemaining { get; set; }
}
