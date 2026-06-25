using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Models;
using CampaignVault.Tools;
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
                if (ToolCallExamples.TryGet(toolName, out _))
                {
                    var (summary, retryExample) =
                        ToolCallExamples.BuildDeserializationErrorResponse(toolName, jsonEx.Message);
                    return ToErrorResult(summary, retryExample);
                }

                return ToErrorResult($"Invalid arguments for '{toolName}': {jsonEx.Message}", null);
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
                "Use a RulesetSystem value: Dnd5e, Pathfinder2e, Fallout2d20, or Narrative.",
            ("commit", "changes") =>
                "Pass an array of world-change objects; each item needs a '$type' field (e.g. event, hp, activity). Call get_help for copy-paste patterns.",
            ("commit", "narrative") =>
                "Provide a short summary of what happened for the event log.",
            ("get_scene", "locationId") =>
                "Use search_world or get_world_state to find a location ID first.",
            ("get_npc_context", "characterId") or ("get_npc_needs", "characterId") =>
                "Use get_scene or search_world to find the exact character ID.",
            ("get_faction_context", "factionId") =>
                "Use get_scene or search_world for exact faction IDs.",
            ("get_quest_details", "questId") =>
                "Use get_scene for active quest summaries, then request the full document.",
            ("search_world", "query") or ("recall_history", "query") =>
                "Pass a name or keyword to search for.",
            ("advance_world", "narrative") =>
                "Summarize the travel, rest, or downtime activity.",
            ("start_combat", "locationId") =>
                "Pass where combat occurs.",
            ("start_combat", "combatantIds") =>
                "Pass an array of character IDs participating in combat.",
            ("upsert_character", "character") =>
                "Pass the full Character object (legacy key 'c' is accepted). systemStats.attributes is numeric-only; put class flavor in notes.",
            ("upsert_location", "location") =>
                "Pass the full Location object. Location.type must be Region, Settlement, District, Building, Room, or Wilderness.",
            ("upsert_lore", "lore") =>
                "Pass the full Lore object.",
            ("define_need_descriptor", "needName") or ("define_need_descriptor", "descriptor") =>
                "Both needName and descriptor are required.",
            ("set_active_system", "activeSystem") =>
                "Use a RulesetSystem value: Dnd5e, Pathfinder2e, Fallout2d20, or Narrative.",
            ("get_current_campaign", "campaignName") =>
                "Pass the campaign slug (e.g. dragon-heist). Call list_campaigns to discover slugs.",
            _ =>
                $"Call list_tools or get_help for the expected argument names and examples."
        };

        if (ToolCallExamples.TryGet(toolName, out _))
        {
            return ToolCallExamples.BuildMissingParamResponse(toolName, paramName, guidance);
        }

        var legacyExample = (toolName, paramName) switch
        {
            ("create_campaign", "name") => "create_campaign(name: \"dragon-heist\", initialSystem: \"Dnd5e\")",
            ("get_scene", "locationId") => "get_scene(\"locations/tavern\")",
            ("get_npc_context", "characterId") => "get_npc_context(\"characters/innkeeper\")",
            ("get_faction_context", "factionId") => "get_faction_context(\"factions/thieves-guild\")",
            ("get_quest_details", "questId") => "get_quest_details(\"quests/rats_01\")",
            ("start_combat", "locationId") => "start_combat(\"locations/tavern\", [\"characters/hero\"])",
            ("define_need_descriptor", _) =>
                "define_need_descriptor(\"homesickness\", \"Longing for home and family.\")",
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
        var text = $"Error: {summary}. Full details in structuredContent.";
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(payload),
        };
    }
}