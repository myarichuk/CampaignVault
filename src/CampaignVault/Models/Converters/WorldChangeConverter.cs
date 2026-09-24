using System.Text.Json.Nodes;

using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Plugins;

namespace CampaignVault.Models.Converters;

/// <summary>
/// JSON preprocessing utility for WorldChange arrays that normalizes missing $type discriminators.
/// Modifies the JSON tree *before* System.Text.Json deserialization, avoiding issues with
/// polymorphic converter metadata.
/// </summary>
internal static class WorldChangeNormalizer
{
    /// <summary>
    /// Preprocesses a JSON string containing WorldChange[] to inject missing $type fields.
    /// If all changes already have $type, returns the input unchanged (no allocations).
    /// If any are missing, parses, infers types, and returns corrected JSON.
    /// </summary>
    public static string NormalizeChangesArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        json = RewriteDiscriminatorAliases(json);

        // Quick optimization: if the string already contains "$type" for every object marker,
        // it likely has all discriminators. Skip parsing in happy path.
        if (!json.Contains("$type", StringComparison.Ordinal))
        {
            // No $type at all; need to parse and infer
            return InferMissingTypes(json);
        }

        // $type exists somewhere; parse to check if all objects have it
        try
        {
            var node = JsonNode.Parse(json) as JsonArray;
            if (node == null || !NeedsNormalization(node))
                return json; // All objects have $type; use original

            return InferMissingTypes(json);
        }
        catch
        {
            // If parsing fails, let the normal deserializer handle the error
            return json;
        }
    }

    private static string RewriteDiscriminatorAliases(string json)
    {
        if (!json.Contains("statusremove", StringComparison.OrdinalIgnoreCase))
        {
            return json;
        }

        return json.Replace("\"statusremove\"", "\"status_remove\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsNormalization(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonObject obj && !obj.ContainsKey("$type"))
                return true;
        }
        return false;
    }

    private static string InferMissingTypes(string json)
    {
        try
        {
            var array = JsonNode.Parse(json) as JsonArray;
            if (array == null)
                return json;

            var modified = false;
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                    continue;

                if (obj.ContainsKey("$type"))
                {
                    var typeVal = obj["$type"];
                    if (typeVal is not null && typeVal.AsValue().GetValue<string>() is { Length: > 0 })
                        continue; // Already has valid $type
                }

                var inferredType = TryInferType(obj);
                if (inferredType != null)
                {
                    obj["$type"] = inferredType;
                    modified = true;

                    System.Diagnostics.Debug.WriteLine(
                        $"WorldChangeNormalizer: Inferred missing $type = '{inferredType}'. " +
                        "This works but indicates the LLM is not including required $type fields.");
                }
                else
                {
                    throw new InvalidOperationException(
                        "WorldChange object is missing the required '$type' discriminator field and we could not infer the type from its properties. " +
                        "Please ensure every change object includes '$type' — see WorldChange's own description for the full list of valid values.");
                }
            }

            return modified ? array.ToJsonString() : json;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            // Parse/modification error; let normal deserializer handle it
            System.Diagnostics.Debug.WriteLine($"WorldChangeNormalizer: Parse error: {ex.Message}");
            return json;
        }
    }

    private static string? TryInferType(JsonObject obj)
    {
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

        return null;
    }
}

public sealed class WorldChangeArrayJsonConverter : JsonConverter<WorldChange[]>
{
    private static readonly object InnerGate = new();
    private static JsonSerializerOptions? _inner;
    private static int _innerRegistryVersion = -1;

    private static JsonSerializerOptions Inner
    {
        get
        {
            lock (InnerGate)
            {
                var version = WorldChangeTypeRegistry.Instance.Version;
                if (_inner is null || _innerRegistryVersion != version)
                {
                    _inner = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowOutOfOrderMetadataProperties = true,
                        TypeInfoResolver = WorldChangeTypeRegistry.Instance.CreateTypeInfoResolver(),
                    };
                    _innerRegistryVersion = version;
                }

                return _inner;
            }
        }
    }

    public override WorldChange[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var json = WorldChangeNormalizer.NormalizeChangesArray(doc.RootElement.GetRawText());
        return JsonSerializer.Deserialize<WorldChange[]>(json, Inner);
    }

    public override void Write(Utf8JsonWriter writer, WorldChange[] value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, Inner);
    }
}
