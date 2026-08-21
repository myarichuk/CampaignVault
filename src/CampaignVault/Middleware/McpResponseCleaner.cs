using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Strips semantic vector fields from MCP tool responses to conserve token context, while
/// keeping them stored in RavenDB for search operations. Also re-serializes the cleaned
/// StructuredContent back into the text Content block, replacing the SDK's raw (vector-laden)
/// dump — most MCP hosts (opencode among them) only forward Content into the model's context,
/// not StructuredContent, so Content has to carry the real, cleaned data rather than a summary
/// stand-in. StructuredContent is left populated too, for hosts that do read it.
/// </summary>
internal static class McpResponseCleaner
{
    // The MCP SDK serializes StructuredContent with a camelCase naming policy
    // (ModelContextProtocol.McpJsonUtilities.DefaultOptions), so the wire keys are
    // "semanticVector"/"embeddingTextHash", not the PascalCase nameof(...) values below.
    // Ordinal comparison would never match those keys, silently defeating this filter.
    private static readonly HashSet<string> VectorFieldsToStrip =
        new(IHasSemanticVector.StrippedFields, StringComparer.OrdinalIgnoreCase);

    // WriteIndented defaults to false, but stated explicitly so a future edit can't silently
    // reintroduce pretty-printed whitespace into every tool response's Content block.
    private static readonly JsonSerializerOptions ContentSerializerOptions = new()
    {
        WriteIndented = false,
    };

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
                    var cleaned = StripVectorsFromElement(result.StructuredContent.Value);
                    result.StructuredContent = cleaned;
                    SyncContentToCleanedStructuredContent(result, cleaned);
                }
                catch
                {
                    // If cleaning fails, return original result
                }
            }

            return result;
        });
    }

    /// <summary>
    /// Replaces the SDK's default Content dump (serialized before vector-stripping ran, so it
    /// still carries raw semanticVector/embeddingTextHash arrays) with a compact re-serialization
    /// of the already-cleaned StructuredContent. Most MCP hosts only forward Content into the
    /// model's context, so this — not StructuredContent — is the copy that actually needs to be
    /// both complete and vector-free.
    /// </summary>
    private static void SyncContentToCleanedStructuredContent(CallToolResult result, JsonElement cleaned)
    {
        var text = JsonSerializer.Serialize(cleaned, ContentSerializerOptions);
        result.Content = [new TextContentBlock { Text = text }];
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
