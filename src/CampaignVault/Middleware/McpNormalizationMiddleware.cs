using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CampaignVault.Middleware;

/// <summary>
/// Middleware to normalize MCP tool call arguments for 'upsert_character', 'upsert_location', and 'upsert_lore'.
/// It automatically handles flattened properties (wrapping them under the expected parameter key)
/// and legacy wrapped parameter names ('c' and 'l' -> 'character' and 'location').
///
/// This is a workaround for Grok Web's stale client-side schema cache, which still sends the original
/// legacy parameter names from an early version of this server. Track at: [link to issue].
/// </summary>
public class McpNormalizationMiddleware(RequestDelegate next, ILogger<McpNormalizationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only run on HTTP POST to the root MCP route
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

                            if (toolName is "upsert_location" or "upsert_character" or "upsert_lore")
                            {
                                if (paramsObj?["arguments"] is JsonObject argumentsObj)
                                {
                                    var expectedKey = toolName switch
                                    {
                                        "upsert_location" => "location",
                                        "upsert_character" => "character",
                                        "upsert_lore" => "lore",
                                        _ => throw new System.InvalidOperationException()
                                    };

                                    var legacyKey = toolName switch
                                    {
                                        "upsert_location" => "l",
                                        "upsert_character" => "c",
                                        _ => null
                                    };

                                    var needsWrapping = false;
                                    var needsRename = false;
                                    string? foundLegacyKey = null;

                                    if (argumentsObj.ContainsKey(expectedKey))
                                    {
                                        // Already correctly wrapped — nothing to do
                                    }
                                    else if (legacyKey != null && argumentsObj.ContainsKey(legacyKey))
                                    {
                                        // Wrapped under legacy key, need to rename
                                        needsRename = true;
                                        foundLegacyKey = legacyKey;
                                    }
                                    else
                                    {
                                        // Flattened, need to wrap
                                        needsWrapping = true;
                                    }

                                    if (needsRename && foundLegacyKey != null)
                                    {
                                        logger.LogDebug(
                                            "McpNormalization: renaming legacy key '{LegacyKey}' → '{ExpectedKey}' for tool '{ToolName}'",
                                            foundLegacyKey, expectedKey, toolName);

                                        var value = argumentsObj[foundLegacyKey];
                                        argumentsObj.Remove(foundLegacyKey);

                                        var clonedValue = JsonNode.Parse(value!.ToJsonString());
                                        argumentsObj.Add(expectedKey, clonedValue);

                                        var modifiedBodyText = rootObj.ToJsonString();
                                        var modifiedBytes = Encoding.UTF8.GetBytes(modifiedBodyText);
                                        context.Request.Body = new MemoryStream(modifiedBytes);
                                    }
                                    else if (needsWrapping)
                                    {
                                        logger.LogDebug(
                                            "McpNormalization: wrapping flattened arguments under '{ExpectedKey}' for tool '{ToolName}'",
                                            expectedKey, toolName);

                                        var wrappedArgs = new JsonObject();
                                        var clonedArgs = JsonNode.Parse(argumentsObj.ToJsonString());
                                        wrappedArgs.Add(expectedKey, clonedArgs);

                                        paramsObj!["arguments"] = wrappedArgs;

                                        var modifiedBodyText = rootObj.ToJsonString();
                                        var modifiedBytes = Encoding.UTF8.GetBytes(modifiedBodyText);
                                        context.Request.Body = new MemoryStream(modifiedBytes);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Parsing failed — reset the body so downstream can still attempt to handle the request.
                // This is a best-effort normalization layer; a bad body here is not a fatal error.
                logger.LogDebug(ex, "McpNormalization: failed to parse or rewrite request body; passing through unchanged");
                context.Request.Body.Position = 0;
            }
        }

        await next(context);
    }
}
