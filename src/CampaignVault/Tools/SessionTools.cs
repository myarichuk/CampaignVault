using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class SessionTools : CampaignToolBase, IMcpServerTool
{
    private readonly CampaignRepository _repo;
    private readonly IPressureOrchestrator _pressureOrchestrator;

    public SessionTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureOrchestrator pressureOrchestrator,
        ILogger<SessionTools>? logger = null)
        : base(repository, keys, logger)
    {
        _repo = repository;
        _pressureOrchestrator = pressureOrchestrator;
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        @"KICKOFF TOOL — CALL ONCE AT SESSION START (or after a reconnect/summarization gap), never per turn. One round-trip returns everything needed to begin play:
- session record + recap of the last session
- campaign context (ruleset system, narrative focus, party roster hint, last event)
- authoritative world state: time, scoped rumors/quests/factions, recent events, WorldPressure (resolve any ENGINE WARNING immediately), and seedCoverage (entity counts + gaps — check right after world_build)
- the full party roster with equipped/carried items
Opens a new session, or resumes the already-open one (resumed:true) — safe to call again after context loss. partyLocationId is optional; omit if unknown and derive it from recent events. During play, refresh state via take_turn instead of re-calling this.")]
    public Task<ToolResult<SessionStartView>> StartSession(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("Optional session title/number.")] string? title = null,
        [Description("Optional current party location ID — anchors world-state scoping. Omit if unknown.")] string? partyLocationId = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var sessionLog = await _repo.GetSessionLogAsync(session, effective);
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
                var time = await _repo.GetTimeAsync(session, effective);

                var record = new SessionLog.SessionRecord
                {
                    Number = newNumber,
                    Title = title,
                    StartedAtUtc = DateTime.UtcNow,
                    InWorldStartDay = (int)time.TotalDaysElapsed,
                    InWorldStartTimeOfDay = time.TimeOfDay.ToString(),
                    IsOpen = true,
                };

                if (sessionLog == null)
                {
                    sessionLog = new SessionLog
                    {
                        Id = $"{effective}/state/sessions",
                        CampaignName = effective
                    };
                }
                sessionLog.Sessions.Add(record);
                await session.StoreAsync(sessionLog, sessionLog.Id);

                view.SessionNumber = newNumber;
            }

            view.LastSessionRecap = sessionLog?.Sessions
                .Where(s => !s.IsOpen && !string.IsNullOrEmpty(s.RecapText))
                .OrderByDescending(s => s.Number)
                .FirstOrDefault()?.RecapText ?? "No prior sessions.";

            // Campaign context (meta + posture); a missing meta doc is unusual but not fatal to kickoff.
            var campaign = await session.LoadAsync<Campaign>(_keys.Meta(effective));
            if (campaign != null)
            {
                var posture = await CampaignPostureBuilder.BuildAsync(session, _repo, _keys, effective, isNewCampaign: false);
                view.Campaign = new CampaignContextView(campaign, posture);
            }

            view.WorldState = await _repo.BuildWorldStateAsync(session, effective, partyLocationId, _pressureOrchestrator);
            view.WorldState.SeedCoverage = await _repo.BuildSeedCoverageAsync(session, effective, partyLocationId);

            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();
            foreach (var member in party)
            {
                var heldItems = await session.Query<Item>()
                    .Where(i => i.HolderId == member.Id && !i.IsArchived)
                    .ToListAsync();
                view.Party.Add(new PartyMemberView(
                    member,
                    heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList(),
                    heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList()));
            }

            var summary = view.Resumed
                ? $"Resumed open session {view.SessionNumber} for campaign '{effective}' ({view.Party.Count} party member(s))."
                : $"Session {view.SessionNumber} started for campaign '{effective}' ({view.Party.Count} party member(s)).";
            if (string.IsNullOrEmpty(partyLocationId))
            {
                summary += " HINT: partyLocationId was not provided — identify the party's location from recent events, then call get_entity with that location ID to load the scene.";
            }
            else if (view.WorldState.PartyLocation == null)
            {
                summary += $" WARNING: partyLocationId '{partyLocationId}' was not found. Verify the correct location ID from recent events.";
            }

            return new ToolResult<SessionStartView>(true, view, summary,
                WorldPressure: view.WorldState.WorldPressure?.ToArray() is { Length: > 0 } wp ? wp : null);
        }, saveChanges: true);
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("End the current open session and store the recap.")]
    public Task<ToolResult<object>> EndSession(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("LLM-authored recap text describing key events and outcomes")] string recapText)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var sessionLog = await _repo.GetSessionLogAsync(session, effective);
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);

            if (openSession == null)
                return new ToolResult<object>(false,
                    Error: "No open session to end.");

            openSession.EndedAtUtc = DateTime.UtcNow;
            var time = await _repo.GetTimeAsync(session, effective);
            openSession.InWorldEndDay = (int)time.TotalDaysElapsed;
            openSession.InWorldEndTimeOfDay = time.TimeOfDay.ToString();
            openSession.RecapText = recapText;
            openSession.IsOpen = false;

            await session.StoreAsync(sessionLog, sessionLog.Id);

            return new ToolResult<object>(true, new
            {
                SessionEnded = new { Number = openSession.Number, Title = openSession.Title },
                RecapStored = true,
            },
            $"Session {openSession.Number} ended and recap stored for campaign '{effective}'.");
        }, saveChanges: true);
    }
}
