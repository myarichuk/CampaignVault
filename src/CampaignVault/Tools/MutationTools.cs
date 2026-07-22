using System.ComponentModel;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.RateLimiting;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase, IMcpServerTool
{
    private readonly IPressureManager _pressureManager;
    private readonly IPressureOrchestrator _pressureOrchestrator;

    // Keyed per-campaign so commits in one campaign never throttle another.
    private static readonly ConcurrentDictionary<string, RateLimiter> CommitRateLimiters = new(StringComparer.OrdinalIgnoreCase);

    private static RateLimiter GetRateLimiter(string campaignName) =>
        CommitRateLimiters.GetOrAdd(campaignName, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 50,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true
        }));

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
Accepts a batch of changes (HP, Items — including persistent damage/wear/hidden-feature details via item_update's upsertItemDetail — Events, Rumors, Relationships, Needs, Attributes, Activity, Status, ruleset_action, and world updates).

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, copy-paste examples, and change-type reference.

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided.**")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Batch of world changes and narrative summary.")]
        CommitRequest request,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        if (request?.Changes is null || request.Changes.Length == 0)
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "changes",
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
                toolName: "commit");
        }

        if (string.IsNullOrWhiteSpace(request.Narrative))
        {
            return ToolArgumentErrors.Missing<CommitResult>(
                "narrative",
                "Provide a short summary of what happened for the event log.",
                toolName: "commit");
        }

        return Commit(request.Changes, request.Narrative, campaignName);
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

        var duplicationConflict = SideEffectDuplicationGuard.FindConflict(changes);
        if (duplicationConflict != null)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.InvalidArgument,
                Summary: $"Commit rejected: {duplicationConflict}"));
        }

        var rateLimiter = GetRateLimiter(effective);
        if (!rateLimiter.AttemptAcquire().IsAcquired)
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
            var stats = rateLimiter.GetStatistics();
            if (stats != null)
                result.RateLimitTokensRemaining = (int)stats.CurrentAvailablePermits;

            var msg = $"World updated with {changes.Length} changes. Full result in structuredContent.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
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

            return new ToolResult<AdvanceResult>(true, result,
                $"Advanced {days} days. {result.SimulatorEvents.Count} events and {allPressureItems.Count} structured pressures generated.",
                WorldPressure: cappedPressure);
        });
    }
}