using System.ComponentModel;
using System.Text.Json;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase
{
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;

    private static readonly RateLimiter CommitRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 50,
        TokensPerPeriod = 10,
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        AutoReplenishment = true
    });

    public MutationTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IPressureManager pressureManager,
        IPressureOrchestrator pressureOrchestrator,
        ILogger<MutationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _pressureManager = pressureManager;
        _pressureOrchestrator = pressureOrchestrator;
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(
        @"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world.
Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove, ruleset_action, and the open-world creates/updates).
Requires campaignName (see get_help → Campaign slug scoping).
Use ActivityChange liberally to keep get_scene in sync with your narrative.

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided (the primary laziness mitigation).**

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, auto-linking, and many more copy-paste examples.

" + CommitTypesReference.SupportedTypesBullet + @"

**Crowd interrupt roll (`scene_interrupt_check`)**: After a tense beat in a location with `ambientCrowd`, optionally commit a single-roll crowd reaction. Supply `riskModifier` (-50..+50) like `encounterRiskModifier` on travel; omit to auto-derive from `visualTags`/appearance. On success the engine promotes ONE transient from the crowd. Cooldown: one interrupt per location per day. Example:
[ { ""$type"": ""scene_interrupt_check"", ""locationId"": ""locations/training-hall"", ""characterId"": ""chars/valen"", ""riskModifier"": 25, ""notes"": ""Bloodied wanted face, crowd hostile"" } ]
" + CommitEnumCheatSheet.Compact + @"

=== RECOMMENDED PATTERNS (copy-paste friendly) ===

" + CommitHelpExamples.ConversationSection + @"

" + CommitHelpExamples.PoiMaterializeSection + @"

(See get_help for the full expanded list including the tavern creation + promotion flow, one-way link fixes, ambient/PoI flavor without bloat, PoI add/materialize/modify/remove + time decay, etc.)

Basic + creating on the fly examples are also shown in the tool description and get_help.")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Array of world changes. Each item must be a JSON object with a '$type' discriminator.")]
        JsonElement? changes,
        [Description("Narrative summary of what happened (for the log and world pressure).")]
        string? narrative)
    {
        if (!CommitChangesParser.TryParse(changes, out var parsedChanges, out var parseError))
        {
            if (parseError is not null)
            {
                var (summary, retryExample) = ToolCallExamples.BuildDeserializationErrorResponse("commit", parseError);
                return Task.FromResult(new ToolResult<CommitResult>(
                    false,
                    Error: ToolErrors.InvalidArgument,
                    Summary: summary,
                    RetryExample: retryExample));
            }

            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        return Commit(parsedChanges!, narrative, campaignName);
    }

    public Task<ToolResult<CommitResult>> Commit(
        WorldChange[]? changes,
        string? narrative = null,
        string? campaignName = null)
    {
        if (changes is null || changes.Length == 0)
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        if (string.IsNullOrWhiteSpace(narrative))
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "narrative",
                "Provide a short summary of what happened for the event log.",
                toolName: "commit");
        }

        if (!TryGetEffectiveCampaign(campaignName, out var effective))
        {
            return Task.FromResult(new ToolResult<CommitResult>(
                false,
                Error: ToolErrors.NoCampaignSelected,
                Summary: NoCampaignSelectedSummary));
        }

        if (changes.Length > 50)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary:
                $"Commit rejected: Too many changes in a single batch ({changes.Length}). Maximum allowed is 50."));
        }

        if (!CommitRateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded,
                Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        return ExecuteAsync(async session =>
        {
            var result = await _repository.StageChangesAsync(session, changes, effective);
            if (!result.Success)
            {
                var errorMsg = "NO CHANGES WERE SAVED — the entire batch was rolled back because at least one " +
                                "change failed validation. Fix the error(s) below and resend the FULL batch " +
                                "(not just the failed item).\n" + string.Join("\n", result.Summary);
                return new ToolResult<CommitResult>(false, result, Summary: errorMsg,
                    Error: "ValidationError");
            }

            var commitTime = await _repository.GetTimeAsync(session, effective);
            await _repository.LogEventAsync(session,
                new Event
                {
                    Id = "events/" + Guid.NewGuid(), CampaignName = effective, Summary = narrative,
                    Category = EventCategory.SceneCommit, Involved = result.InvolvedEntities,
                    DayLogged = (int)commitTime.TotalDaysElapsed
                }, effective);

            // Warn if the batch contained combat/status mutations but no narrative event
            var hasCombatMutation = changes.Any(c => c is HpChange or RulesetAction or StatusChange);
            var hasNarrativeEvent = changes.Any(c => c is EventOccurred);
            if (hasCombatMutation && !hasNarrativeEvent)
            {
                result.NarrativeReminder =
                    "This commit included combat/status changes but no 'event' ($type: event). " +
                    "Add an EventOccurred to record the narrative beat for future get_npc_context and recall_history queries.";
            }

            // Surface remaining rate-limit budget so the LLM can pace large scenes
            var stats = CommitRateLimiter.GetStatistics();
            if (stats != null)
                result.RateLimitTokensRemaining = (int)stats.CurrentAvailablePermits;

            var msg = $"World updated with {changes.Length} changes. Full result in structuredContent.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
    }

    /// <summary>
    /// Fallback for callers (or future clients) that can only easily emit a raw JSON string for the changes batch.
    /// Parses to WorldChange[] and delegates to the primary MCP Commit implementation.
    /// Not exposed as an MCP tool.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(changesJson);
        }
        catch (JsonException ex)
        {
            var (summary, retryExample) = ToolCallExamples.BuildDeserializationErrorResponse("commit", ex.Message);
            return Task.FromResult(new ToolResult<CommitResult>(
                false,
                Error: ToolErrors.InvalidArgument,
                Summary: summary,
                RetryExample: retryExample));
        }

        return Commit(campaignName ?? string.Empty, json, narrative);
    }


    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "TIME PASSAGE: Call for travel, long rests, or downtime. Fast-forwards the world clock and runs simulation rules. Requires campaignName.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Number of days to skip.")]
        int days,
        [Description("The resulting time of day.")]
        TimeOfDay timeOfDay,
        [Description("Summary of the rest or travel activity.")]
        string narrative,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        if (days <= 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest",
                Summary: "Cannot advance zero or a negative number of days."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var result = await _repository.AdvanceWorldAsync(session, days, timeOfDay, effective);

            var partyIds = await session.Query<Character>()
                .Where(c => c.CampaignName == effective && (c.IsPc || c.IsPartyCompanion))
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
                    DaysAdvanced: days,
                    DisableCooldowns: true));

            var rawPressures = result.SimulatorEvents
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

            return new ToolResult<AdvanceResult>(true, result,
                $"Advanced {days} days. {result.SimulatorEvents.Count} events and {allPressureItems.Count} structured pressures generated.",
                WorldPressure: cappedPressure);
        });
    }
}