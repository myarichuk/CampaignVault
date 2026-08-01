using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Middleware;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Verifies McpSchemaDeduplicator actually shrinks repeated schema subtrees, and — critically —
/// that resolving every "$ref" back against "$defs" reproduces exactly the original schema.
/// A dedup bug that silently changes the effective schema would be far worse than the size cost
/// it's meant to save, since it would corrupt what the model is told about a tool's shape.
/// </summary>
public class McpSchemaDeduplicatorTests
{
    private const string BigSystemStatsShape =
        """{"type":"object","properties":{"strength":{"type":"integer"},"dexterity":{"type":"integer"},"constitution":{"type":"integer"},"intelligence":{"type":"integer"},"wisdom":{"type":"integer"},"charisma":{"type":"integer"},"armorClass":{"type":"integer"},"skillModifiers":{"type":"object","additionalProperties":{"type":"integer"}},"savingThrowModifiers":{"type":"object","additionalProperties":{"type":"integer"}},"hitDie":{"type":["string","null"]},"level":{"type":["integer","null"]}}}""";

    [Fact]
    public void LeavesSmallOrUniqueSchemasUnchanged()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement;

        var result = McpSchemaDeduplicator.Deduplicate(schema);

        Assert.Equal(schema.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void DeduplicatesRepeatedLargeSubtree_AndShrinksTotalSize()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"changes\":{\"anyOf\":[" +
            "{\"type\":\"object\",\"properties\":{\"$type\":{\"const\":\"character_update\"},\"characterId\":{\"type\":\"string\"},\"systemStats\":" + BigSystemStatsShape + "}}," +
            "{\"type\":\"object\",\"properties\":{\"$type\":{\"const\":\"system_stats\"},\"characterId\":{\"type\":\"string\"},\"systemStats\":" + BigSystemStatsShape + "}}" +
            "]}}}").RootElement;

        var result = McpSchemaDeduplicator.Deduplicate(schema);

        Assert.True(result.GetRawText().Length < schema.GetRawText().Length,
            "deduplicated schema should be smaller than the original");
        Assert.True(result.TryGetProperty("$defs", out var defs));
        Assert.Single(defs.EnumerateObject());

        // Both branches' systemStats should now be $ref pointers, not inlined duplicates.
        var branches = result.GetProperty("properties").GetProperty("changes").GetProperty("anyOf");
        foreach (var branch in branches.EnumerateArray())
        {
            var systemStats = branch.GetProperty("properties").GetProperty("systemStats");
            Assert.True(systemStats.TryGetProperty("$ref", out var refProp));
            Assert.StartsWith("#/$defs/", refProp.GetString());
        }
    }

    [Fact]
    public void ResolvingRefsReproducesTheExactOriginalSchema()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"changes\":{\"anyOf\":[" +
            "{\"type\":\"object\",\"properties\":{\"$type\":{\"const\":\"character_update\"},\"characterId\":{\"type\":\"string\"},\"systemStats\":" + BigSystemStatsShape + "}}," +
            "{\"type\":\"object\",\"properties\":{\"$type\":{\"const\":\"system_stats\"},\"characterId\":{\"type\":\"string\"},\"systemStats\":" + BigSystemStatsShape + "}}," +
            "{\"type\":\"object\",\"properties\":{\"$type\":{\"const\":\"activity\"},\"characterId\":{\"type\":\"string\"},\"newActivity\":{\"type\":\"string\"}}}" +
            "]}}}").RootElement;

        var deduped = McpSchemaDeduplicator.Deduplicate(schema);
        var resolved = ResolveRefs(deduped);

        // Structural equality via re-serialized, key-order-normalized JSON.
        Assert.Equal(Normalize(schema.GetRawText()), Normalize(resolved.ToJsonString()));
    }

    [Fact]
    public void HandlesDeduplicationInsideOutputSchemaArrayItems()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{" +
            "\"npcs\":{\"type\":\"array\",\"items\":" + BigSystemStatsShape + "}," +
            "\"party\":{\"type\":\"array\",\"items\":" + BigSystemStatsShape + "}" +
            "}}").RootElement;

        var result = McpSchemaDeduplicator.Deduplicate(schema);
        var resolved = ResolveRefs(result);

        Assert.True(result.GetRawText().Length < schema.GetRawText().Length);
        Assert.Equal(Normalize(schema.GetRawText()), Normalize(resolved.ToJsonString()));
    }

    private static JsonNode ResolveRefs(JsonElement schemaElement)
    {
        var root = JsonNode.Parse(schemaElement.GetRawText())!;
        var defs = root["$defs"] as JsonObject;
        return ResolveNode(root, defs)!;
    }

    private static JsonNode? ResolveNode(JsonNode? node, JsonObject? defs)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj.TryGetPropertyValue("$ref", out var refNode) &&
                refNode is JsonValue refVal && refVal.TryGetValue<string>(out var refPath))
            {
                var defName = refPath.Replace("#/$defs/", "");
                Assert.NotNull(defs);
                Assert.True(defs!.TryGetPropertyValue(defName, out var target));
                return ResolveNode(target!.DeepClone(), defs);
            }

            var result = new JsonObject();
            foreach (var (key, value) in obj)
            {
                if (key == "$defs")
                {
                    continue;
                }

                result[key] = ResolveNode(value, defs);
            }

            return result;
        }

        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
            {
                result.Add(ResolveNode(item, defs));
            }

            return result;
        }

        return node?.DeepClone();
    }

    /// <summary>Re-parses/re-serializes with sorted object keys so structurally-equal-but-differently-ordered JSON compares equal.</summary>
    private static string Normalize(string json)
    {
        var node = JsonNode.Parse(json);
        return NormalizeNode(node).ToJsonString();
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                sorted[key] = NormalizeNode(obj[key]);
            }

            return sorted;
        }

        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
            {
                result.Add(NormalizeNode(item));
            }

            return result;
        }

        return node?.DeepClone();
    }
}
