using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
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

            // Campaign context (meta + posture) — existence already confirmed above.
            var posture = await CampaignPostureBuilder.BuildAsync(session, _repo, _keys, effective, isNewCampaign: false);
            view.Campaign = new CampaignContextView(existingCampaign, posture);

            view.WorldState = await _repo.BuildWorldStateAsync(session, effective, partyLocationId, _pressureOrchestrator);
            view.WorldState.SeedCoverage = await _repo.BuildSeedCoverageAsync(session, effective, partyLocationId);

            var party = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();

            if (party.Count == 0)
            {
                return new ToolResult<SessionStartView>(false, Error: ToolErrors.InvalidArgument,
                    Summary: $"Campaign '{effective}' has no party members. Seed at least one character with IsPc via world_build before start_session.");
            }

            // Ensure all party members have upgraded SystemStats before building party views.
            if (party.Count > 0)
            {
                var partyDict = party.ToDictionary(m => m.Id);
                await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                    session, partyDict, effective, _keys);
            }

            foreach (var member in party)
            {
                var heldItems = await session.Query<Item>()
                    .Where(i => i.HolderId == member.Id && !i.IsArchived)
                    .ToListAsync();
                view.Party.Add(new PartyMemberView(
                    CharacterDetailView.From(member),
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
            var sessionLog = await _repo.GetSessionLogAsync(new CampaignSession(session, effective));
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);

            if (openSession == null || sessionLog == null)
                return new ToolResult<object>(false,
                    Error: "No open session to end.");

            openSession.EndedAtUtc = DateTime.UtcNow;
            var time = await _repo.GetTimeAsync(new CampaignSession(session, effective));
            openSession.InWorldEndDay = (int)time.TotalDaysElapsed;
            openSession.InWorldEndTimeOfDay = time.GetTimeOfDayName();
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
