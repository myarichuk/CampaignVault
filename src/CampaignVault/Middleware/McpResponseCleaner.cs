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
///
/// The same tree walk also drops properties that carry no information beyond their own absence —
/// empty containers ("field": [] / "field": {}) and explicit nulls ("field": null) — bottom-up, so a
/// container that becomes empty purely from its children being stripped is caught in the same pass.
/// Every response DTO here defaults its collections to non-null-but-empty rather than null, so this
/// is pure wire-format hygiene, never a content decision. Present-but-falsy scalars ("", 0, false)
/// ARE kept: those are readings, and an LLM must be able to tell "HP 0" from "HP not reported".
/// Array *elements* are never removed either — an array's length is content.
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
            Apply(result);
            return result;
        });
    }

    /// <summary>
    /// Cleans a tool result in place: parses its structured content once, strips it, and writes the one
    /// cleaned tree back out to BOTH copies the protocol carries — compact text for Content (replacing
    /// the SDK's own dump, which was serialized before stripping ran and so still carries raw
    /// semanticVector/embeddingTextHash arrays) and a JsonElement for StructuredContent.
    ///
    /// Internal rather than private so tests exercise the real filter path end-to-end. They previously
    /// reflected on two private halves, which meant a change to how those halves fit together — exactly
    /// the kind of change that breaks the wire format — could not fail a test.
    /// </summary>
    internal static void Apply(CallToolResult result)
    {
        if (result.IsError == true || result.StructuredContent == null)
        {
            return;
        }

        try
        {
            var cleaned = Clean(result.StructuredContent.Value);
            if (cleaned == null)
            {
                return;
            }

            // Content first: it is the copy most MCP hosts actually forward to the model, and
            // serializing the cleaned node straight to text skips a whole JsonElement round-trip
            // over what can be a very large payload.
            result.Content = [new TextContentBlock { Text = cleaned.ToJsonString(ContentSerializerOptions) }];
            result.StructuredContent = JsonSerializer.SerializeToElement(cleaned);
        }
        catch
        {
            // If cleaning fails, leave the original result alone rather than dropping the response.
        }
    }

    /// <summary>
    /// Parses the SDK's structured content once and strips it in place, returning the mutated tree.
    /// Null for scalar payloads (nothing to strip) so the caller leaves the result untouched.
    /// </summary>
    internal static JsonNode? Clean(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when JsonNode.Parse(element.GetRawText()) is JsonObject obj:
                StripVectorsFromObject(obj);
                return obj;

            case JsonValueKind.Array when JsonNode.Parse(element.GetRawText()) is JsonArray arr:
                StripVectorsFromArray(arr);
                return arr;

            default:
                return null;
        }
    }

    private static void StripVectorsFromObject(JsonObject obj)
    {
        // One pass: decide each property's fate (drop / recurse) and collect the drops, so the object
        // is only mutated once at the end. Enumerating and removing in the same loop needs a defensive
        // ToList() copy of the whole property set, which this avoids.
        var keysToRemove = new List<string>();

        foreach (var kvp in obj)
        {
            if (VectorFieldsToStrip.Contains(kvp.Key))
            {
                keysToRemove.Add(kvp.Key);
                continue;
            }

            switch (kvp.Value)
            {
                case null:
                // An explicit null says exactly what an absent key says, but costs the key name plus
                // ":null" in every response. The delta-mode trims (e.g. ApplyLocationDeltaTrim) reset
                // roughly a dozen fields per location to null precisely because the client already has
                // them, so without this the "trim" still pays most of the wire cost it was added to
                // avoid.
                case JsonValue v when v.GetValueKind() == JsonValueKind.Null:
                    keysToRemove.Add(kvp.Key);
                    break;

                case JsonObject nested:
                    StripVectorsFromObject(nested);
                    if (nested.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                    break;

                case JsonArray array:
                    StripVectorsFromArray(array);
                    if (array.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                    break;
            }
        }

        foreach (var key in keysToRemove)
        {
            obj.Remove(key);
        }
    }

    /// <summary>
    /// Cleans every element of an array. Nested arrays recurse too — the previous version only
    /// descended into elements that were objects, so a vector sitting inside an array-of-arrays (or an
    /// object one array deeper) survived the strip entirely.
    /// Elements themselves are never removed: an array's length is content (three combatants is not
    /// the same as two), unlike an absent property.
    /// </summary>
    private static void StripVectorsFromArray(JsonArray array)
    {
        foreach (var element in array)
        {
            switch (element)
            {
                case JsonObject objInArray:
                    StripVectorsFromObject(objInArray);
                    break;
                case JsonArray nestedArray:
                    StripVectorsFromArray(nestedArray);
                    break;
            }
        }
    }
}
