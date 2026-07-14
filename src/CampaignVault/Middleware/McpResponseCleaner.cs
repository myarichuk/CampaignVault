using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Strips semantic vector fields from MCP tool responses to conserve token context,
/// while keeping them stored in RavenDB for search operations.
/// </summary>
internal static class McpResponseCleaner
{
    private static readonly HashSet<string> VectorFieldsToStrip =
        new(IHasSemanticVector.StrippedFields, StringComparer.Ordinal);

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
            }

            return result;
        });
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
