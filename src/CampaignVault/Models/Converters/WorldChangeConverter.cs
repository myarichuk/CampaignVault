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
        json = RewriteFieldAliases(json);

        // Quick optimization: if the string already contains "$type" for every object marker,
        // it likely has all discriminators. Skip parsing in happy path.
        if (!json.Contains("$type", StringComparison.Ordinal))
        {
            // No $type at all; need to parse and infer
            return InferMissingTypes(json);
        }

        // $type exists somewhere; parse to check if all objects have it
        JsonArray? node;
        try
        {
            node = JsonNode.Parse(json) as JsonArray;
        }
        catch
        {
            // If parsing fails, let the normal deserializer handle the error
            return json;
        }

        if (node == null || !NeedsNormalization(node))
            return json; // All objects have $type; use original

        return InferMissingTypes(json);
    }

    private static string RewriteDiscriminatorAliases(string json)
    {
        if (!json.Contains("statusremove", StringComparison.OrdinalIgnoreCase))
        {
            return json;
        }

        // Token-aware: only the $type discriminator value "statusremove" (any casing) becomes
        // "status_remove". A raw string Replace would also rewrite narratives and property
        // values that merely mention it.
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            return json; // let the normal deserializer report the error
        }

        if (node is not JsonArray array)
        {
            return json;
        }

        var modified = false;
        foreach (var item in array)
        {
            if (item is JsonObject obj
                && obj.TryGetPropertyValue("$type", out var typeVal)
                && typeVal is JsonValue val
                && val.TryGetValue<string>(out var discriminator)
                && discriminator.Equals("statusremove", StringComparison.OrdinalIgnoreCase))
            {
                obj["$type"] = "status_remove";
                modified = true;
            }
        }

        return modified ? array.ToJsonString() : json;
    }

    /// <summary>
    /// LLMs occasionally send <c>actorId</c> where a change wants <c>characterId</c> (a documented naming
    /// slip). Rename it when <c>characterId</c> is absent and drop it otherwise, so the strict deserializer
    /// (unknown fields fail the batch) doesn't reject a change whose meaning is unambiguous.
    /// </summary>
    private static string RewriteFieldAliases(string json)
    {
        if (!json.Contains("actorId", StringComparison.Ordinal))
            return json;

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch
        {
            return json;
        }

        if (array is null)
            return json;

        var modified = false;
        foreach (var entry in array)
        {
            if (entry is not JsonObject obj || !obj.TryGetPropertyValue("actorId", out var actorIdNode))
                continue;

            obj.Remove("actorId");
            if (!obj.ContainsKey("characterId"))
                obj["characterId"] = actorIdNode?.DeepClone();
            modified = true;
        }

        return modified ? array.ToJsonString() : json;
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
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject obj)
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
                    // A JsonException so both commit paths (CommitChangesParser and the MCP binding filter)
                    // turn it into the same short, listed-$types message (CommitBindingError).
                    throw new JsonException($"Change $[{i}] {Tools.CommitBindingError.MissingTypeMarker}. Path: $[{i}]");
                }
            }

            return modified ? array.ToJsonString() : json;
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            // Parse/modification error; let normal deserializer handle it
            System.Diagnostics.Debug.WriteLine($"WorldChangeNormalizer: Parse error: {ex.Message}");
            return json;
        }
    }

    private static string? TryInferType(JsonObject obj)
    {
        // Each inference must name a $type whose declared fields cover the keys that triggered it: the
        // binder runs with UnmappedMemberHandling.Disallow, so a wrong guess fails the whole batch with a
        // confusing "unknown member" error instead of the clear missing-$type one.
        if (obj.ContainsKey("characterId"))
        {
            if (obj.ContainsKey("newActivity") || obj.ContainsKey("updateLocation") || obj.ContainsKey("newLocationId"))
                return "activity"; // ActivityChange owns newLocationId; TravelChange uses destinationLocationId.
            if (obj.ContainsKey("destinationLocationId"))
                return "travel";
            if (obj.ContainsKey("targetId") && obj.ContainsKey("verb"))
                return "engagement_relation";
            if (obj.ContainsKey("poolName"))
                return "resource";
            if (obj.ContainsKey("need"))
                return "need";
            if (obj.ContainsKey("status") || obj.ContainsKey("effect"))
                return "status";
            if (obj.ContainsKey("delta"))
                return "hp"; // HpChange has no newValue; delta alone (after need/resource above) is HP.
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
                        // Same strictness as CommitChangesParser.CreateOptions: unknown fields fail loudly.
                        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
