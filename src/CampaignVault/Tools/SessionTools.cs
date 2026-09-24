using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class SessionTools : CampaignToolBase, IMcpServerTool
{
    private const int MaxNpcSuggestions = 3;

    private readonly CampaignRepository _repo;
    private readonly IPressureOrchestrator _pressureOrchestrator;
    private readonly IEnumerable<IPluginTraitsUpgrader> _traitsUpgraders;

    public SessionTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureOrchestrator pressureOrchestrator,
        ILogger<SessionTools>? logger = null,
        IEnumerable<IPluginTraitsUpgrader>? traitsUpgraders = null)
        : base(repository, keys, logger)
    {
        _repo = repository;
        _pressureOrchestrator = pressureOrchestrator;
        _traitsUpgraders = traitsUpgraders ?? [];
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Call once at session start (or after a reconnect/context loss), never per turn. Returns your last handoff, campaign posture, time, quests, WorldPressure (fix ENGINE WARNINGs immediately) and the party from the DB. Resumes an open session.")]
    public Task<ToolResult<SessionStartView>> StartSession(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("Optional session title/number.")] string? title = null,
        [Description("Optional party location ID; defaults to the PC's DB location.")] string? partyLocationId = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            // A campaign only exists once it's gone through create_campaign/finalize_campaign_onboarding.
            // Without this check, a typo'd or never-onboarded slug silently opened a session and
            // auto-created a starter party for a campaign with no meta document at all (invisible to
            // list_campaigns) — dangerous on a server shared across multiple clients/campaigns.
            var existingCampaign = await session.LoadAsync<Campaign>(_keys.Meta(effective));
            if (existingCampaign == null)
            {
                var allCampaigns = await session.Query<Campaign>()
                    .Where(c => c.Id != null && c.Id.StartsWith("campaigns/") && c.Id.EndsWith("/meta"))
                    .ToListAsync();
                var suggestions = CampaignSlugMatcher.FindSuggestions(effective, allCampaigns,
                    c => new CampaignSuggestion(
                        CampaignSlug.TryCanonicalize(c.Name, out var slug) ? slug : c.Name,
                        c.DisplayName ?? c.Name,
                        c.System,
                        0,
                        null));
                var hint = suggestions.Count > 0
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Slug} ({s.DisplayName})"))
                    : " Call list_campaigns to see existing campaigns, or create_campaign/start_campaign_onboarding to create a new one.";
                return new ToolResult<SessionStartView>(false, Error: ToolErrors.SlugNotFound,
                    Summary: $"Campaign '{effective}' does not exist.{hint}");
            }

            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            if (party.Count == 0)
            {
                return new ToolResult<SessionStartView>(false, Error: ToolErrors.InvalidArgument,
                    Summary: $"Campaign '{effective}' has no party members. Seed at least one character with IsPc via world_build before start_session.");
            }

            var sessionLog = await _repo.GetSessionLogAsync(new CampaignSession(session, effective));
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);

            var view = new SessionStartView { Title = title };

            if (openSession != null)
            {
                view.SessionNumber = openSession.Number;
                view.Title ??= openSession.Title;
                view.Resumed = true;
            }
            else
            {
                var newNumber = (sessionLog?.Sessions.Count ?? 0) + 1;
                var time = await _repo.GetTimeAsync(new CampaignSession(session, effective));

                var record = new SessionLog.SessionRecord
                {
                    Number = newNumber,
                    Title = title,
                    StartedAtUtc = DateTime.UtcNow,
                    InWorldStartDay = (int)time.TotalDaysElapsed,
                    InWorldStartTimeOfDay = time.GetTimeOfDayName(),
                    IsOpen = true,
                };

                sessionLog ??= new SessionLog
                {
                    Id = $"{effective}/state/sessions",
                    CampaignName = effective
                };
                sessionLog.Sessions.Add(record);
                await session.StoreAsync(sessionLog, sessionLog.Id);

                view.SessionNumber = newNumber;
            }

            // Ensure all party members have upgraded SystemStats before reading AC/level off them.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, party.ToDictionary(m => m.Id), effective, _keys, traitsUpgraders: _traitsUpgraders, logger: _logger);

            var pcLocationId = party.Where(m => m.IsPc).Select(m => m.CurrentLocationId).FirstOrDefault(l => !string.IsNullOrEmpty(l))
                               ?? party.Select(m => m.CurrentLocationId).FirstOrDefault(l => !string.IsNullOrEmpty(l));
            var scopeLocationId = string.IsNullOrEmpty(partyLocationId) ? pcLocationId : partyLocationId;

            var posture = await CampaignPostureBuilder.BuildAsync(session, _repo, _keys, effective, isNewCampaign: false);
            view.Campaign = SessionCampaignView.From(existingCampaign, posture);

            var worldState = await _repo.BuildWorldStateAsync(session, effective, scopeLocationId, _pressureOrchestrator);
            view.Time = worldState.Time.FormattedDate;
            view.ActiveQuests = worldState.ActiveQuests?.Select(SessionQuestView.From).ToList() is { Count: > 0 } quests ? quests : null;

            var seedCoverage = await _repo.BuildSeedCoverageAsync(session, effective, scopeLocationId);
            view.SeedCoverage = seedCoverage.Gaps.Count > 0 ? seedCoverage : null;

            var handoffRecord = SelectCurrentHandoff(sessionLog!);
            if (handoffRecord != null)
            {
                view.Handoff = SessionHandoffView.From(handoffRecord);
            }
            else
            {
                var lastRecap = sessionLog!.Sessions
                    .Where(s => !s.IsOpen && !string.IsNullOrEmpty(s.RecapText))
                    .MaxBy(s => s.Number);
                view.RecentDigest = SessionDigestBuilder.Build(lastRecap?.Number, lastRecap?.RecapText, worldState.RecentEvents);
            }

            // Item names only — projected in the query so full item documents never load.
            var memberIds = party.Select(m => m.Id).ToList();
            var heldItems = await session.Query<Item>()
                .Where(i => i.HolderId.In(memberIds) && !i.IsArchived)
                .Select(i => new SessionItemName { HolderId = i.HolderId, Name = i.Name, IsEquipped = i.IsEquipped })
                .ToListAsync();
            var itemsByHolder = heldItems.ToLookup(i => i.HolderId, StringComparer.OrdinalIgnoreCase);

            view.Party = party
                .OrderByDescending(m => m.IsPc)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => PartySessionView.From(m, itemsByHolder[m.Id].OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();

            view.PartyFingerprint = PartyFingerprint.Compute(party);
            await PrimeTurnCursorAsync(session, effective, view.PartyFingerprint);

            return new ToolResult<SessionStartView>(true, view,
                BuildStartSummary(effective, view, sessionLog!, partyLocationId, worldState.PartyLocation, scopeLocationId),
                WorldPressure: worldState.WorldPressure?.ToArray() is { Length: > 0 } wp ? wp : null);
        }, saveChanges: true);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "End the session. handoff = {storySoFar (max 800, fold in the previous one), lastSession (max 600), openThreads (max 6x120), npcsInPlay (max 8x{id, stance max 80}), partyIntent (max 200), tone (max 120)}; start_session returns it next. checkpoint:true keeps the session open. Guide: lookup kind=help topic=sessions.")]
    public Task<ToolResult<object>> EndSession(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("Structured session summary, like a context compaction. Required: lastSession. Caps: storySoFar 800, lastSession 600, openThreads 6×120, npcsInPlay 8 (stance 80), partyIntent 200, tone 120.")] SessionHandoff? handoff = null,
        [Description("Deprecated alias for handoff.lastSession.")] string? recapText = null,
        [Description("true = store the handoff but keep the session open.")] bool checkpoint = false)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var sessionLog = await _repo.GetSessionLogAsync(new CampaignSession(session, effective));
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);

            if (openSession == null || sessionLog == null)
                return new ToolResult<object>(false, Error: ToolErrors.InvalidArgument,
                    Summary: "No open session to end. Call start_session first.");

            handoff ??= new SessionHandoff();
            if (string.IsNullOrWhiteSpace(handoff.LastSession) && !string.IsNullOrWhiteSpace(recapText))
            {
                handoff.LastSession = recapText;
            }

            var problems = SessionHandoffRules.NormalizeAndValidate(handoff);
            problems.AddRange(await ValidateNpcsInPlayAsync(session, effective, handoff.NpcsInPlay));
            if (problems.Count > 0)
            {
                return new ToolResult<object>(false, Error: ToolErrors.InvalidArgument,
                    Summary: "Handoff not stored — fix and resend (nothing was saved): " + string.Join(" ", problems));
            }

            // A missing storySoFar keeps the previous fold rather than dropping the campaign's history.
            var storyCarriedOver = false;
            if (handoff.StorySoFar == null)
            {
                handoff.StorySoFar = sessionLog.Sessions
                    .Where(s => s.Handoff?.StorySoFar != null)
                    .MaxBy(s => s.Number)?.Handoff!.StorySoFar;
                storyCarriedOver = handoff.StorySoFar != null;
            }

            openSession.Handoff = handoff;
            openSession.HandoffIsCheckpoint = checkpoint;
            openSession.HandoffWrittenAtUtc = DateTime.UtcNow;
            openSession.RecapText = handoff.LastSession;

            if (!checkpoint)
            {
                openSession.EndedAtUtc = DateTime.UtcNow;
                var time = await _repo.GetTimeAsync(new CampaignSession(session, effective));
                openSession.InWorldEndDay = (int)time.TotalDaysElapsed;
                openSession.InWorldEndTimeOfDay = time.GetTimeOfDayName();
                openSession.IsOpen = false;
            }

            await session.StoreAsync(sessionLog, sessionLog.Id);

            var summary = checkpoint
                ? $"Checkpoint stored for session {openSession.Number} of '{effective}'; the session stays open. A resumed start_session returns it."
                : $"Session {openSession.Number} of '{effective}' ended; the handoff will open session {openSession.Number + 1}.";
            if (storyCarriedOver)
            {
                summary += " storySoFar was omitted, so the previous one was kept unchanged — fold this session into it next time.";
            }

            return new ToolResult<object>(true, new
            {
                Session = openSession.Number,
                Checkpoint = checkpoint,
                HandoffChars = HandoffLength(handoff),
            }, summary);
        }, saveChanges: true);
    }

    /// <summary>The newest stored handoff, unless a later session closed without one (legacy recapText-only
    /// data): then that handoff is stale and start_session falls back to the digest.</summary>
    internal static SessionLog.SessionRecord? SelectCurrentHandoff(SessionLog sessionLog)
    {
        var withHandoff = sessionLog.Sessions.Where(s => s.Handoff != null).MaxBy(s => s.Number);
        if (withHandoff == null)
        {
            return null;
        }

        var newerClosedWithout = sessionLog.Sessions.Any(s => !s.IsOpen && s.Handoff == null && s.Number > withHandoff.Number);
        return newerClosedWithout ? null : withHandoff;
    }

    /// <summary>
    /// A new conversation has none of the delta baseline take_turn assumes (already-surfaced NPC stubs,
    /// need baselines), so the first take_turn after start_session must be Full. Also records the kickoff
    /// fingerprint so echoing it on that first take_turn isn't read as drift. A campaign with no cursor yet
    /// needs neither: its first take_turn is Full by definition.
    /// </summary>
    private async Task PrimeTurnCursorAsync(IAsyncDocumentSession session, string effective, string fingerprint)
    {
        // A new conversation hasn't seen the one-shot guidance hints: teach each once per session.
        var ledger = await session.LoadAsync<GuidanceLedger>(_keys.StateGuidance(effective));
        ledger?.Delivered.Clear();

        var cursor = await _repo.GetTurnCursorAsync(new CampaignSession(session, effective));
        if (cursor == null)
        {
            return;
        }

        cursor.ForcedFullReseedPending = true;
        cursor.LedgerResetPending = true;
        cursor.LastPartyFingerprint = fingerprint;
    }

    private async Task<List<string>> ValidateNpcsInPlayAsync(IAsyncDocumentSession session, string effective, List<NpcStance>? npcs)
    {
        if (npcs is not { Count: > 0 })
        {
            return [];
        }

        var ids = npcs.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var loaded = await session.LoadAsync<Character>(ids);
        var unknown = ids
            .Where(id => loaded.GetValueOrDefault(id) is not { } c
                         || !(string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective))
            .ToList();
        if (unknown.Count == 0)
        {
            return [];
        }

        var candidates = await session.Query<Character>()
            .Where(c => c.CampaignName == effective && !c.IsPc && !c.IsPartyCompanion)
            .Select(c => new CharacterIdName { Id = c.Id, Name = c.Name })
            .Take(1024)
            .ToListAsync();

        return unknown.Select(id =>
        {
            var close = SuggestCharacters(id, candidates);
            return close.Count > 0
                ? $"npcsInPlay id '{id}' is not a character in this campaign; did you mean {string.Join(", ", close)}?"
                : $"npcsInPlay id '{id}' is not a character in this campaign; use search_world to find the right id, or leave it out.";
        }).ToList();
    }

    internal sealed class CharacterIdName
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private static List<string> SuggestCharacters(string requestedId, List<CharacterIdName> candidates)
    {
        var wanted = Tokens(requestedId[(requestedId.LastIndexOf('/') + 1)..]);
        if (wanted.Count == 0)
        {
            return [];
        }

        return candidates
            .Select(c => (c, Score: Tokens(c.Id[(c.Id.LastIndexOf('/') + 1)..]).Concat(Tokens(c.Name)).Distinct()
                .Count(t => wanted.Any(w => t.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(t, StringComparison.Ordinal)))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.c.Id, StringComparer.Ordinal)
            .Take(MaxNpcSuggestions)
            .Select(x => $"{x.c.Id} ({x.c.Name})")
            .ToList();
    }

    private static List<string> Tokens(string text) =>
        text.ToLowerInvariant()
            .Split(['-', '_', ' ', '/', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToList();

    private static int HandoffLength(SessionHandoff h) =>
        (h.StorySoFar?.Length ?? 0) + (h.LastSession?.Length ?? 0) + (h.PartyIntent?.Length ?? 0) + (h.Tone?.Length ?? 0)
        + (h.OpenThreads?.Sum(t => t.Length) ?? 0) + (h.NpcsInPlay?.Sum(n => n.Id.Length + (n.Stance?.Length ?? 0)) ?? 0);

    private static string BuildStartSummary(
        string effective,
        SessionStartView view,
        SessionLog sessionLog,
        string? requestedLocationId,
        LocationSummary? resolvedLocation,
        string? sceneLocationId)
    {
        var summary = view.Resumed
            ? $"Resumed open session {view.SessionNumber} for campaign '{effective}' ({view.Party.Count} party member(s))."
            : $"Session {view.SessionNumber} started for campaign '{effective}' ({view.Party.Count} party member(s)).";

        var priorSessions = sessionLog.Sessions.Any(s => s.Number < view.SessionNumber);
        if (view.Handoff == null && priorSessions)
        {
            summary += " No handoff stored for the last session — recentDigest is a server fallback. Call end_session with a handoff when this session ends.";
        }

        if (!string.IsNullOrEmpty(requestedLocationId) && resolvedLocation == null)
        {
            summary += $" WARNING: partyLocationId '{requestedLocationId}' was not found; the party's DB locations are in party[].locationId.";
        }
        else if (!string.IsNullOrEmpty(sceneLocationId))
        {
            summary += $" Load the scene: take_turn with fullDetailLocationId={sceneLocationId} and clientPartyFingerprint (no get_entity needed).";
        }
        else
        {
            summary += " No party member has a location yet — set one via take_turn (travel or character_update) before narrating a scene.";
        }

        return summary;
    }
}
