using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Data.Pressure;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MutationTools : CampaignToolBase
{
    private readonly IPressureManager _pressureManager;

    private static readonly RateLimiter CommitRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 10000, 
        TokensPerPeriod = 1000, 
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        AutoReplenishment = true
    });

    public MutationTools(
        CampaignRepository repository, 
        ICurrentCampaignContext currentCampaign, 
        CampaignDocumentKeys keys,
        IPressureManager pressureManager) 
        : base(repository, currentCampaign, keys)
    {
        _pressureManager = pressureManager;
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(@"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world.
Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove, ruleset_action, and the open-world creates/updates). 
Use ActivityChange liberally to keep get_scene in sync with your narrative. 

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided (the primary laziness mitigation).**

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, auto-linking, and many more copy-paste examples.

Supported types for $type: hp, item, item_update, status, statusremove, event, rumor, relationship, engagement_relation, spatial_position, need, attribute, mood, activity, ruleset_action, location_create, location_update, character_create, character_update, system_stats, knowledge_update, schedule_change, item_create, travel, rest, faction_create, faction_reputation, faction_state, quest_create, quest_progress.
" + CommitEnumCheatSheet.Compact + @"

=== RECOMMENDED PATTERNS (copy-paste friendly) ===

**Conversation (REQUIRED: `involved` with every speaker — NOT `participants`):**
[
  { ""$type"": ""event"", ""category"": ""Conversation"", ""summary"": ""Valen asked Lirael about missing caravans on the Gold Road."", ""involved"": [""chars/valen"", ""chars/lirael-goldvein""] },
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/valen"", ""targetId"": ""chars/lirael-goldvein"", ""category"": ""Social"", ""verb"": ""discussing the disappearances with"", ""bidirectional"": true },
  { ""$type"": ""activity"", ""characterId"": ""chars/valen"", ""newActivity"": ""Listening intently at the bar"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/lirael-goldvein"", ""newActivity"": ""Sharing guarded information over the bar"" },
  { ""$type"": ""knowledge_update"", ""characterId"": ""chars/valen"", ""topic"": ""Caravan Disappearances on the Gold Road"", ""details"": ""Three caravans vanished without trace near Whispering Pass."", ""source"": ""Heard"", ""valence"": ""Negative"", ""urgency"": ""High"", ""importance"": ""Important"" }
]

(See get_help for the full expanded list including the tavern creation + promotion flow, one-way link fixes, ambient/PoI flavor without bloat, etc.)

Basic + creating on the fly examples are also shown in the tool description and get_help.")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Array of world changes. Each item must be a JSON object with a '$type' discriminator.")] JsonElement? changes = null,
        [Description("Narrative summary of what happened (for the log and world pressure).")] string? narrative = null,
        [Description("Optional campaign name. Falls back to currently selected campaign.")] string? campaignName = null)
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

        var effective = EffectiveCampaign(campaignName);

        if (changes.Length > 50)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: $"Commit rejected: Too many changes in a single batch ({changes.Length}). Maximum allowed is 50."));
        }

        if (!CommitRateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        return ExecuteAsync(async session => {
            var result = await _repository.StageChangesAsync(session, changes, effective);
            if (!result.Success)
            {
                var errorMsg = string.Join("\n", result.Summary);
                return new ToolResult<CommitResult>(false, result, Summary: "Commit failed due to validation errors.", Error: errorMsg);
            }
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), CampaignName = effective, Summary = narrative, Category = EventCategory.SceneCommit, Involved = result.InvolvedEntities }, effective);
            var msg = $"World updated with {changes.Length} changes.";
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

        return Commit(json, narrative, campaignName);
    }

    [ToolCategory("Mutation & time")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("TIME PASSAGE: Call this for travel, long rests, or downtime. Fast-forwards the world clock and runs background simulations (rumor decay, NPC needs). Returns narrative updates on what changed while the party was away. Respects the currently selected campaign.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Number of days to skip.")] int days, 
        [Description("The resulting time of day.")] TimeOfDay timeOfDay,
        [Description("Summary of the rest or travel activity.")] string narrative,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        if (days < 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest", Summary: "Cannot advance a negative number of days."));
        }

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var result = await _repository.AdvanceWorldAsync(session, days, timeOfDay, effective);
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.Timeskip });

            var timeDoc = await _repository.GetTimeAsync(session, effective);
            
            string[]? cappedPressure = null;
            var rawPressures = result.SimulatorEvents
                .Select(e => new WorldPressureItem(PressureSeverity.Simulation, "Simulation", e, ExplorationTools.EventGroupingKey))
                .Concat(result.WorldPressure)
                .ToList();

            if (rawPressures.Count > 0)
            {
                cappedPressure = await _pressureManager.FilterAndCapAsync(session, effective, (int)timeDoc.TotalDaysElapsed, rawPressures);
            }

            return new ToolResult<AdvanceResult>(true, result, 
                $"Advanced {days} days. {result.SimulatorEvents.Count} events and {result.WorldPressure.Count} structured pressures generated.",
                WorldPressure: cappedPressure != null && cappedPressure.Length > 0 ? cappedPressure : null);
        });
    }
}
