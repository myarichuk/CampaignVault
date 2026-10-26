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
        json = EnsureDiscriminators(json);
        SweepUnknownFields(json);
        return json;
    }

    private static string EnsureDiscriminators(string json)
    {
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

    /// <summary>
    /// Verb names a model reaches for that mean an existing <c>$type</c>. Compared case-insensitively against
    /// the discriminator value only, never against narratives or property values.
    /// </summary>
    private static readonly Dictionary<string, string> DiscriminatorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["statusremove"] = "status_remove",
        ["remove_status"] = "status_remove",
        ["item_transfer"] = "item",
        ["transfer_item"] = "item",
        ["itemtransfer"] = "item",
        ["create_character"] = "character_create",
        ["charactercreate"] = "character_create",
        ["create_item"] = "item_create",
        ["itemcreate"] = "item_create",
        ["create_rumor"] = "rumor_create",
        ["rumorcreate"] = "rumor_create",
        ["create_location"] = "location_update",
        ["location_create"] = "location_update",
        ["knowledge"] = "knowledge_update",
    };

    /// <summary>
    /// Field names a model reaches for, per <c>$type</c> ("*" applies to every type), mapped to the real field.
    /// The alias is renamed when the real field is absent and dropped when it is present, so the strict binder
    /// (unknown fields fail the batch) never rejects a change whose meaning is unambiguous.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> FieldAliases = new(StringComparer.Ordinal)
    {
        ["*"] = new(StringComparer.OrdinalIgnoreCase) { ["actorId"] = "characterId" },
        ["status_remove"] = new(StringComparer.OrdinalIgnoreCase) { ["statusName"] = "status", ["condition"] = "status", ["name"] = "status" },
        ["status"] = new(StringComparer.OrdinalIgnoreCase) { ["statusName"] = "status", ["condition"] = "status" },
        ["knowledge_update"] = new(StringComparer.OrdinalIgnoreCase) { ["content"] = "details", ["text"] = "details", ["memory"] = "details", ["summary"] = "details" },
        ["engagement_relation"] = new(StringComparer.OrdinalIgnoreCase) { ["end"] = "clear", ["remove"] = "clear", ["ended"] = "clear" },
        ["event"] = new(StringComparer.OrdinalIgnoreCase) { ["participants"] = "involved", ["description"] = "summary", ["text"] = "summary" },
        ["travel"] = new(StringComparer.OrdinalIgnoreCase) { ["toLocationId"] = "destinationLocationId", ["destinationId"] = "destinationLocationId", ["locationId"] = "destinationLocationId", ["newLocationId"] = "destinationLocationId" },
        ["item"] = new(StringComparer.OrdinalIgnoreCase) { ["holderId"] = "toHolderId", ["newHolderId"] = "toHolderId", ["toCharacterId"] = "toHolderId" },
        ["item_create"] = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "itemId", ["toHolderId"] = "holderId" },
        ["character_create"] = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "characterId", ["locationId"] = "currentLocationId" },
        ["location_update"] = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "locationId" },
        ["rumor_create"] = new(StringComparer.OrdinalIgnoreCase) { ["id"] = "rumorId" },
        ["hp"] = new(StringComparer.OrdinalIgnoreCase) { ["amount"] = "delta", ["change"] = "delta" },
    };

    private static readonly string[] AliasProbe =
        [.. DiscriminatorAliases.Keys.Concat(FieldAliases.Values.SelectMany(m => m.Keys)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string RewriteDiscriminatorAliases(string json)
    {
        if (!DiscriminatorAliases.Keys.Any(k => json.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return json;
        }

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
                && DiscriminatorAliases.TryGetValue(discriminator, out var canonical))
            {
                obj["$type"] = canonical;
                modified = true;
            }
        }

        return modified ? array.ToJsonString() : json;
    }

    private static string RewriteFieldAliases(string json)
    {
        if (!AliasProbe.Any(k => json.Contains(k, StringComparison.OrdinalIgnoreCase)))
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
            if (entry is not JsonObject obj)
                continue;

            var discriminator = obj.TryGetPropertyValue("$type", out var t) && t is JsonValue tv && tv.TryGetValue<string>(out var d) ? d : null;
            modified |= ApplyAliases(obj, FieldAliases["*"]);
            if (discriminator is not null && FieldAliases.TryGetValue(discriminator, out var perType))
                modified |= ApplyAliases(obj, perType);
        }

        return modified ? array.ToJsonString() : json;
    }

    private static bool ApplyAliases(JsonObject obj, Dictionary<string, string> aliases)
    {
        var modified = false;
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (!aliases.TryGetValue(key, out var canonical))
                continue;

            var value = obj[key];
            obj.Remove(key);
            var hasCanonical = obj.Any(kv => string.Equals(kv.Key, canonical, StringComparison.OrdinalIgnoreCase));
            if (!hasCanonical)
                obj[canonical] = value?.DeepClone();
            modified = true;
        }

        return modified;
    }

    /// <summary>
    /// Reports every unknown top-level field across the whole array in one error, instead of the binder's
    /// first-failure-only exception: a batch with three misnamed fields costs one round trip, not three.
    /// Types the schema model doesn't know (an unregistered $type) are left to the binder.
    /// </summary>
    private static void SweepUnknownFields(string json)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch
        {
            return;
        }

        if (array is null)
            return;

        List<(int Index, string Type, string Field, string? Suggestion)>? unknown = null;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject obj
                || !obj.TryGetPropertyValue("$type", out var t) || t is not JsonValue tv || !tv.TryGetValue<string>(out var discriminator))
                continue;

            var variant = Schema.CommitSchemaModel.Find(discriminator);
            if (variant is null)
                continue;

            var known = new HashSet<string>(variant.Fields.Select(f => f.JsonName), StringComparer.OrdinalIgnoreCase) { "$type", "minutesElapsed" };
            foreach (var key in obj.Select(kv => kv.Key))
            {
                if (known.Contains(key))
                    continue;
                unknown ??= [];
                unknown.Add((i, discriminator, key, Tools.CommitBindingError.Suggest(key, variant.Fields.Select(f => f.JsonName))));
            }
        }

        if (unknown is { Count: > 0 })
        {
            throw new JsonException(Tools.CommitBindingError.DescribeUnknownFields(unknown));
        }
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
        if (obj.ContainsKey("itemId") && obj.ContainsKey("holderId") && obj.ContainsKey("name"))
            return "item_create";
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
