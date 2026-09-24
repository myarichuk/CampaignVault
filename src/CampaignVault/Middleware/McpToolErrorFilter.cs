using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Models;
using CampaignVault.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Safety net: converts MCP argument-binding failures into structured ToolResult errors
/// that LLM clients can read and self-correct from, instead of unhandled exceptions.
/// </summary>
internal static partial class McpToolErrorFilter
{
    [GeneratedRegex(@"missing a value for the required parameter '([^']+)'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MissingParamRegex();

    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (ArgumentException ex) when (MissingParamRegex().IsMatch(ex.Message))
            {
                var paramName = MissingParamRegex().Match(ex.Message).Groups[1].Value;
                var toolName = request.Params?.Name ?? "unknown";
                var (summary, retryExample) = BuildMissingParamResponse(toolName, paramName);
                return ToErrorResult(summary, retryExample);
            }
            catch (Exception ex) when (TryUnwrapJsonException(ex, out var jsonEx))
            {
                var toolName = request.Params?.Name ?? "unknown";
                var enrichedMessage = ModelEnumErrorHints.Enrich(jsonEx, null);

                if (ToolCallExamples.TryGet(toolName, out _))
                {
                    var (summary, retryExample) =
                        ToolCallExamples.BuildDeserializationErrorResponse(toolName, enrichedMessage);
                    return ToErrorResult(summary, retryExample);
                }

                return ToErrorResult($"Invalid arguments for '{toolName}': {enrichedMessage}", null);
            }
        });
    }

    internal static (string Summary, JsonElement? RetryExample) BuildMissingParamResponse(string toolName,
        string paramName)
    {
        var guidance = (toolName, paramName) switch
        {
            ("create_campaign", "name") =>
                "Provide a unique campaign slug (spaces become hyphens). Pass campaignName on subsequent tool calls.",
            ("create_campaign", "initialSystem") =>
                $"Use a ruleset system id: {RulesetSystem.Dnd5e}, {RulesetSystem.Pathfinder2e}, or {RulesetSystem.Narrative} (plugins may add more).",
            ("take_turn", "changes") =>
                "Pass an array of world-change objects in request.changes; each item needs a '$type' field (e.g. event, hp, activity). Consult your dnd-* skills for verbs, or call lookup(kind: \"commit_schema\", type: \"<$type>\") for one type's fields.",
            ("take_turn", "narrative") =>
                "Provide a short summary of what happened for the event log (required when changes are present).",
            ("get_entity", "entityId") =>
                "Pass an exact entity ID with its type prefix (chars/, locations/, factions/, quests/, items/, plot-threads/). Use search_world to find IDs.",
            ("search_world", "query") or ("recall_history", "query") =>
                "Pass a name or keyword to search for.",
            ("advance_world", "narrative") =>
                "Summarize the travel, rest, or downtime activity.",
            ("combat", "action") =>
                "Pass 'start', 'next', 'end', or 'status'.",
            ("combat", "locationId") =>
                "action:'start' requires locationId — pass where combat occurs.",
            ("combat", "combatantIds") =>
                "action:'start' requires combatantIds — an array of character IDs participating in combat.",
            ("world_build", "batch") =>
                "Pass an object with one or more arrays: locations, factions, creatures, spells, feats, characters, items, quests, plotThreads, lore, rumors, needDescriptors. Each array uses the same field shape as its live-play commit type (e.g. characters[] entries mirror character_update fields). See lookup kind=help topic=world-building.",
            ("world_build", "campaignName") =>
                "Pass the campaign slug (e.g. dragon-heist). Call list_campaigns to discover slugs.",
            ("lookup", "kind") =>
                "Pass one of: handbook, spells (requires className), creatures, items, item_tags, level_up (requires characterId), commit_schema, help.",
            ("end_session", "recapText") =>
                "Provide an LLM-authored recap of key events and outcomes from the session.",
            _ =>
                $"Call lookup with kind=help, topic=tools for the full catalog and expected argument names."
        };

        if (ToolCallExamples.TryGet(toolName, out _))
        {
            return ToolCallExamples.BuildMissingParamResponse(toolName, paramName, guidance);
        }

        var legacyExample = (toolName, paramName) switch
        {
            ("create_campaign", "name") => "create_campaign(name: \"dragon-heist\", initialSystem: \"Dnd5e\")",
            ("get_entity", "entityId") => "get_entity(\"chars/innkeeper\")",
            ("combat", "locationId") or ("combat", "combatantIds") =>
                "combat(action: \"start\", locationId: \"locations/tavern\", combatantIds: [\"chars/hero\"])",
            ("end_session", "recapText") =>
                "end_session(campaignName: \"dragon-heist\", recapText: \"The party cleared the cellar and negotiated a truce with the goblins.\")",
            _ => null
        };

        var summary = legacyExample is null
            ? $"Missing required parameter '{paramName}' for tool '{toolName}'. {guidance}"
            : $"Missing required parameter '{paramName}'. {guidance} Example: {legacyExample}";

        return (summary, null);
    }

    internal static string BuildMissingParamMessage(string toolName, string paramName) =>
        BuildMissingParamResponse(toolName, paramName).Summary;

    internal static bool TryUnwrapJsonException(Exception ex, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonException? jsonException)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is JsonException jsonEx)
            {
                jsonException = jsonEx;
                return true;
            }
        }

        jsonException = null;
        return false;
    }

    private static CallToolResult ToErrorResult(string summary, JsonElement? retryExample)
    {
        var payload = new ToolResult<object>(false, Error: ToolErrors.InvalidArgument, Summary: summary,
            RetryExample: retryExample);
        var payloadElement = JsonSerializer.SerializeToElement(payload, McpJsonUtilities.DefaultOptions);

        // Full payload goes in Content, matching McpResponseCleaner's success-path rule: most MCP hosts
        // (opencode among them) only forward Content, never StructuredContent, so a short "see
        // structuredContent" stub would point most callers at data they never receive.
        // StructuredContent is populated too only under the same IncludeStructuredContent opt-in.
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = payloadElement.GetRawText() }],
            StructuredContent = McpResponseCleaner.IncludeStructuredContent ? payloadElement : null,
        };
    }
}