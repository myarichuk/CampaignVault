using Microsoft.AspNetCore.Http;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CampaignVault.Middleware;

/// <summary>
/// Middleware to normalize MCP tool call arguments for 'upsert_character', 'upsert_location', and 'upsert_lore'.
/// It automatically handles flattened properties (wrapping them under the expected parameter key)
/// and legacy wrapped parameter names ('c' and 'l' -> 'character' and 'location').
/// </summary>
public class McpNormalizationMiddleware
{
    private readonly RequestDelegate _next;

    public McpNormalizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

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
                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
                {
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

                                if (toolName == "upsert_location" || toolName == "upsert_character" || toolName == "upsert_lore")
                                {
                                    var argumentsObj = paramsObj?["arguments"] as JsonObject;
                                    if (argumentsObj != null)
                                    {
                                        string expectedKey = toolName switch
                                        {
                                            "upsert_location" => "location",
                                            "upsert_character" => "character",
                                            "upsert_lore" => "lore",
                                            _ => throw new System.InvalidOperationException()
                                        };

                                        string? legacyKey = toolName switch
                                        {
                                            "upsert_location" => "l",
                                            "upsert_character" => "c",
                                            _ => null
                                        };

                                        bool needsWrapping = false;
                                        bool needsRename = false;
                                        string? foundLegacyKey = null;

                                        if (argumentsObj.ContainsKey(expectedKey))
                                        {
                                            // Already correctly wrapped
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
            }
            catch
            {
                context.Request.Body.Position = 0;
            }
        }

        await _next(context);
    }
}
