using System.Text;
using System.Text.Json.Nodes;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;

namespace CampaignVault.Middleware;

/// <summary>
/// Middleware to normalize MCP tool call arguments before binding.
/// Handles synonym rewrites (npcId→characterId), legacy upsert wrappers (l→location),
/// flattened upsert payloads, and systemStats sent without the campaign's "$system" marker.
/// </summary>
public class McpNormalizationMiddleware(RequestDelegate next, ILogger<McpNormalizationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == "POST" &&
            CampaignVault.Schema.ToolProfiles.IsMcpRoute(context.Request.Path.Value) &&
            context.Request.ContentType != null &&
            context.Request.ContentType.Contains("application/json"))
        {
            context.Request.EnableBuffering();
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);

                var bodyText = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    var rootNode = JsonNode.Parse(bodyText);
                    if (rootNode is JsonObject rootObj)
                    {
                        var method = rootObj["method"]?.ToString();
                        if (method == "tools/call")
                        {
                            var paramsObj = rootObj["params"] as JsonObject;
                            var toolName = paramsObj?["name"]?.ToString();

                            if (toolName is not null && paramsObj?["arguments"] is JsonObject argumentsObj)
                            {
                                var modified = ToolCallExamples.TryNormalize(toolName, argumentsObj, out var normalized);
                                var rewrites = normalized.ToList();
                                var unmarked = ToolCallExamples.SystemStatsWithoutMarker(toolName, argumentsObj);
                                if (unmarked.Count > 0
                                    && await ActiveStatsSystemAsync(context, argumentsObj) is { } system)
                                {
                                    ToolCallExamples.ApplySystemMarker(unmarked, system, rewrites);
                                    modified = true;
                                }

                                if (rewrites.Count > 0)
                                {
                                    // Information, not Debug: which aliases fire in real play is what decides the alias table.
                                    logger.LogInformation(
                                        "McpNormalization: applied {RewriteCount} rewrite(s) for tool '{ToolName}': {Rewrites}",
                                        rewrites.Count,
                                        toolName,
                                        string.Join(", ", rewrites));
                                }

                                // Only swap the body when a rewrite happened: re-serializing every untouched
                                // take_turn payload (tens of KB on a full reseed) was pure CPU, and the swapped
                                // stream must carry its own length so downstream readers never trust the
                                // original Content-Length against a body of a different size.
                                if (modified)
                                {
                                    var modifiedBodyText = rootObj.ToJsonString();
                                    var modifiedBytes = Encoding.UTF8.GetBytes(modifiedBodyText);
                                    context.Request.Body = new MemoryStream(modifiedBytes);
                                    context.Request.ContentLength = modifiedBytes.Length;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "McpNormalization: failed to parse or rewrite request body; passing through unchanged");
                context.Request.Body.Position = 0;
            }
        }

        await next(context);
    }

    /// <summary>The campaign's ruleset when it has typed stats (dnd5e/pf2e); null when unknown, so binding reports it.</summary>
    private static async Task<string?> ActiveStatsSystemAsync(HttpContext context, JsonObject arguments)
    {
        if (arguments["campaignName"]?.GetValueKind() != System.Text.Json.JsonValueKind.String
            || arguments["campaignName"]!.GetValue<string>() is not { Length: > 0 } campaignName)
        {
            return null;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        var keys = context.RequestServices.GetRequiredService<CampaignDocumentKeys>();
        using var session = store.OpenAsyncSession();
        var config = await session.LoadAsync<CampaignConfig>(keys.Config(campaignName), context.RequestAborted);
        return config?.ActiveSystem is RulesetSystem.Dnd5e or RulesetSystem.Pathfinder2e ? config.ActiveSystem : null;
    }
}