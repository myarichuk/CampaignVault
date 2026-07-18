using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class SessionTools : CampaignToolBase, IMcpServerTool
{
    private readonly CampaignRepository _repo;

    public SessionTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        ILogger<SessionTools>? logger = null)
        : base(repository, keys, logger)
    {
        _repo = repository;
    }

    [ToolCategory("Session & exploration")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("Start a new session and retrieve the recap of the last session (if any).")]
    public Task<ToolResult<object>> StartSession(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("Optional session title/number")] string? title = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) => {
            var sessionLog = await _repo.GetSessionLogAsync(session, effective);
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);

            if (openSession != null)
                return new ToolResult<object>(false,
                    Error: $"Session {openSession.Number} is already open. End it first with end_session.");

            var newNumber = (sessionLog?.Sessions.Count ?? 0) + 1;
            var now = DateTime.UtcNow;
            var time = await _repo.GetTimeAsync(session, effective);

            var record = new SessionLog.SessionRecord
            {
                Number = newNumber,
                Title = title,
                StartedAtUtc = now,
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

            var lastRecap = sessionLog.Sessions
                .Where(s => !s.IsOpen && !string.IsNullOrEmpty(s.RecapText))
                .OrderByDescending(s => s.Number)
                .FirstOrDefault();

            return new ToolResult<object>(true, new
            {
                SessionStarted = new { Number = newNumber, Title = title },
                LastSessionRecap = lastRecap?.RecapText ?? "No prior sessions.",
            },
            $"Session {newNumber} started for campaign '{effective}'.");
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
