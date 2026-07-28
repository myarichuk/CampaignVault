using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CampaignVault.Models.Converters;

/// <summary>
/// Custom converter for WorldChange[] that attempts to infer missing $type discriminators
/// from object properties before delegating to the default polymorphic deserializer.
/// This provides defensive robustness when LLMs omit the required $type field.
/// </summary>
internal class WorldChangeArrayConverter : JsonConverter<WorldChange[]>
{
    public override WorldChange[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Parse the entire array into a JsonNode tree so we can inspect/modify it
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected WorldChange[] to be a JSON array");
        }

        // Convert to mutable JsonNode and try to infer missing $type fields
        var nodeArray = JsonNode.Parse(root.GetRawText()) as JsonArray;
        if (nodeArray == null)
        {
            throw new JsonException("Failed to parse WorldChange array");
        }

        var inferredAny = false;
        foreach (var node in nodeArray)
        {
            if (node is not JsonObject changeObj)
            {
                continue;
            }

            // Skip if $type is already present
            if (changeObj.ContainsKey("$type"))
            {
                var typeVal = changeObj["$type"];
                if (typeVal is not null && typeVal.AsValue().TryGetValue(out string? typeStr) && !string.IsNullOrWhiteSpace(typeStr))
                {
                    continue;
                }
            }

            // Try to infer the type from the object's properties
            var inferredType = TryInferType(changeObj);
            if (inferredType != null)
            {
                changeObj["$type"] = inferredType;
                inferredAny = true;
            }
            else
            {
                // Cannot infer — throw helpful error
                throw new JsonException(
                    "WorldChange object is missing the required '$type' discriminator field and we could not infer the type from its properties. " +
                    "Please ensure every change object includes '$type' with one of: event, hp, engagement_relation, activity, status, status_remove, resource, rumor, quest_progress, plot_thread_progress, plot_thread_clue, location_update, character_update, travel, rest, etc.");
            }
        }

        // If we inferred any types, log that this happened (for debugging)
        if (inferredAny)
        {
            System.Diagnostics.Debug.WriteLine(
                "WorldChangeArrayConverter: Inferred one or more missing $type discriminators. " +
                "This works but indicates the LLM is not including required $type fields — check tool metadata.");
        }

        // Now deserialize with the corrected array
        var json = nodeArray.ToJsonString();
        var defaultOptions = new JsonSerializerOptions(options)
        {
            Converters = { } // Don't include this converter in the recursive call
        };

        return JsonSerializer.Deserialize<WorldChange[]>(json, defaultOptions);
    }

    public override void Write(Utf8JsonWriter writer, WorldChange[]? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        var defaultOptions = new JsonSerializerOptions(options)
        {
            Converters = { } // Exclude this converter
        };

        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, item.GetType(), defaultOptions);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Attempts to infer WorldChange type from characteristic fields.
    /// This is a fallback heuristic; ideally $type should always be present.
    /// </summary>
    private static string? TryInferType(JsonObject obj)
    {
        // Check for fields that distinctly identify a change type
        if (obj.ContainsKey("characterId"))
        {
            if (obj.ContainsKey("delta") && obj.ContainsKey("newValue"))
                return "hp";

            if (obj.ContainsKey("newActivity"))
                return "activity";

            if (obj.ContainsKey("targetId") && obj.ContainsKey("verb"))
                return "engagement_relation";

            if (obj.ContainsKey("poolName"))
                return "resource";

            if (obj.ContainsKey("status") || obj.ContainsKey("effect"))
                return "status";

            if (obj.ContainsKey("updateLocation"))
                return "activity";

            if (obj.ContainsKey("newLocationId"))
                return "travel";
        }

        if (obj.ContainsKey("category") && obj.ContainsKey("summary"))
            return "event";

        if (obj.ContainsKey("itemId") && obj.ContainsKey("toHolderId"))
            return "item";

        if (obj.ContainsKey("rumorId"))
            return "rumor";

        if (obj.ContainsKey("questId"))
            return "quest_progress";

        if (obj.ContainsKey("plotThreadId") && obj.ContainsKey("clueId"))
            return "plot_thread_clue";

        if (obj.ContainsKey("locationId"))
        {
            if (obj.ContainsKey("materializePointOfInterest"))
                return "location_update";
        }

        // Cannot infer
        return null;
    }
}
