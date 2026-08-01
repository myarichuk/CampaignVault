using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Strips semantic vector fields from MCP tool responses to conserve token context,
/// while keeping them stored in RavenDB for search operations. Also collapses the redundant
/// text Content block down to just the narrative Summary when StructuredContent is present —
/// the MCP SDK always serializes the full return value into Content as a text fallback for
/// clients that don't read StructuredContent, which otherwise doubles the effective payload
/// size of every structured tool response.
/// </summary>
internal static class McpResponseCleaner
{
    // The MCP SDK serializes StructuredContent with a camelCase naming policy
    // (ModelContextProtocol.McpJsonUtilities.DefaultOptions), so the wire keys are
    // "semanticVector"/"embeddingTextHash", not the PascalCase nameof(...) values below.
    // Ordinal comparison would never match those keys, silently defeating this filter.
    private static readonly HashSet<string> VectorFieldsToStrip =
        new(IHasSemanticVector.StrippedFields, StringComparer.OrdinalIgnoreCase);

    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);

            // Only process successful tool calls with structured content
            if (result.IsError != true && result.StructuredContent != null)
            {
                try
                {
                    result.StructuredContent = StripVectorsFromElement(result.StructuredContent.Value);
                }
                catch
                {
                    // If cleaning fails, return original result
                }

                TryCollapseContentToSummary(result);
            }

            return result;
        });
    }

    /// <summary>
    /// Replaces the SDK's default full-JSON-dump Content block with just the ToolResult's
    /// Summary text, since StructuredContent already carries the complete data. Leaves Content
    /// untouched if no non-empty "summary" string is present, so nothing is ever silently dropped
    /// to an empty response.
    /// </summary>
    private static void TryCollapseContentToSummary(CallToolResult result)
    {
        if (result.StructuredContent is not { } structured || structured.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!structured.TryGetProperty("summary", out var summaryProp) ||
            summaryProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var summary = summaryProp.GetString();
        if (string.IsNullOrEmpty(summary))
        {
            return;
        }

        result.Content = [new TextContentBlock { Text = summary }];
    }

    private static JsonElement StripVectorsFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var obj = JsonNode.Parse(element.GetRawText()) as JsonObject;
            if (obj != null)
            {
                StripVectorsFromObject(obj);
                return JsonSerializer.SerializeToElement(obj);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonNode.Parse(element.GetRawText()) as JsonArray;
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonObject objInArr)
                    {
                        StripVectorsFromObject(objInArr);
                    }
                }
                return JsonSerializer.SerializeToElement(arr);
            }
        }

        return element;
    }

    private static void StripVectorsFromObject(JsonObject obj)
    {
        var keysToRemove = obj
            .Where(kvp => VectorFieldsToStrip.Contains(kvp.Key))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            obj.Remove(key);
        }

        // Recursively clean nested objects and arrays
        foreach (var kvp in obj.ToList())
        {
            if (kvp.Value is JsonObject nested)
            {
                StripVectorsFromObject(nested);
            }
            else if (kvp.Value is JsonArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject objInArray)
                    {
                        StripVectorsFromObject(objInArray);
                    }
                }
            }
        }
    }
}
