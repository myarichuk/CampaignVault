using System.Text;
using System.Text.Json.Nodes;
using CampaignVault.Tools;

namespace CampaignVault.Middleware;

/// <summary>
/// Middleware to normalize MCP tool call arguments before binding.
/// Handles synonym rewrites (npcId→characterId), legacy upsert wrappers (l→location),
/// and flattened upsert payloads.
/// </summary>
public class McpNormalizationMiddleware(RequestDelegate next, ILogger<McpNormalizationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == "POST" &&
            context.Request.Path == "/" &&
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
                                ToolCallExamples.TryNormalize(toolName, argumentsObj, out var rewrites);
                                if (rewrites.Count > 0)
                                {
                                    logger.LogDebug(
                                        "McpNormalization: applied {RewriteCount} rewrite(s) for tool '{ToolName}': {Rewrites}",
                                        rewrites.Count,
                                        toolName,
                                        string.Join(", ", rewrites));
                                }

                                var modifiedBodyText = rootObj.ToJsonString();
                                var modifiedBytes = Encoding.UTF8.GetBytes(modifiedBodyText);
                                context.Request.Body = new MemoryStream(modifiedBytes);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "McpNormalization: failed to parse or rewrite request body; passing through unchanged");
                context.Request.Body.Position = 0;
            }
        }

        await next(context);
    }
}