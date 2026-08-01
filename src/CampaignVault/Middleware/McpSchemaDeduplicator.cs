using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Deduplicates repeated schema subtrees into "$defs" and replaces the duplicates with "$ref", per
/// tool per schema (input/output are deduplicated independently). System.Text.Json's schema
/// exporter fully inlines every occurrence of a type with no reuse - e.g. the polymorphic
/// SystemExtension (dnd5e/pf2e/narrative ruleset stats) is currently inlined once per
/// WorldChange variant that references it (character_update, ...), and again in
/// world_build's characters[].systemStats.
///
/// This is purely mechanical: it never touches a [Description] attribute or changes what a
/// property means, only how many times an identical subtree is repeated in the tools/list
/// payload. A $ref-aware JSON Schema consumer resolves it to the exact same effective schema.
/// </summary>
internal static class McpSchemaDeduplicator
{
    // Below this, a "$ref": "#/$defs/xxx" pointer (≈25 chars) costs about as much as just
    // inlining the subtree again — only worth deduplicating genuinely large, repeated blocks.
    private const int MinSubtreeCharsToDedupe = 200;

    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);

            foreach (var tool in result.Tools)
            {
                try
                {
                    if (tool.InputSchema.ValueKind == JsonValueKind.Object)
                    {
                        tool.InputSchema = Deduplicate(tool.InputSchema);
                    }
                }
                catch
                {
                    // never let a dedup bug break tool discovery — worst case, schema stays as-is.
                }

                try
                {
                    if (tool.OutputSchema is { ValueKind: JsonValueKind.Object } outputSchema)
                    {
                        tool.OutputSchema = Deduplicate(outputSchema);
                    }
                }
                catch
                {
                    // ignored (the same reason as above)
                }
            }

            return result;
        });
    }

    internal static JsonElement Deduplicate(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText());
        if (root is null)
        {
            return schema;
        }

        var hashOf = new Dictionary<JsonNode, string>(ReferenceEqualityComparer.Instance);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var canonicalText = new Dictionary<string, string>(StringComparer.Ordinal);

        ComputeHashes(root, hashOf, counts, canonicalText);

        var repeated = counts
            .Where(kv => kv.Value >= 2 && canonicalText.ContainsKey(kv.Key))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        
        if (repeated.Count == 0)
        {
            return schema;
        }

        var defs = new JsonObject();
        var defNameByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root is JsonObject or JsonArray)
        {
            RewriteChildrenInPlace(root, hashOf, repeated, canonicalText, defs, defNameByHash);
        }

        if (root is JsonObject rootObj && defs.Count > 0)
        {
            rootObj["$defs"] = defs;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Bottom-up: hashes every node by structure, and records occurrence counts for
    /// object nodes that look like schema definitions (carry "properties") and are large enough
    /// to be worth deduplicating.</summary>
    private static string ComputeHashes(
        JsonNode? node,
        Dictionary<JsonNode, string> hashOf,
        Dictionary<string, int> counts,
        Dictionary<string, string> canonicalText)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var parts = new List<string>();
                foreach (var (key, value) in obj)
                {
                    parts.Add(key + "" + ComputeHashes(value, hashOf, counts, canonicalText));
                }

                parts.Sort(StringComparer.Ordinal);
                var hash = Hash("OBJ:" + string.Join("", parts));
                hashOf[node] = hash;

                if (obj.ContainsKey("properties"))
                {
                    var text = obj.ToJsonString();
                    if (text.Length >= MinSubtreeCharsToDedupe)
                    {
                        counts[hash] = counts.GetValueOrDefault(hash) + 1;
                        canonicalText.TryAdd(hash, text);
                    }
                }

                return hash;
            }
            case JsonArray arr:
            {
                var parts = arr.Select(v => ComputeHashes(v, hashOf, counts, canonicalText)).ToList();
                var hash = Hash("ARR:" + string.Join("", parts));
                hashOf[node] = hash;
                return hash;
            }

            default:
            {
                var leafHash = Hash("LEAF:" + (node?.ToJsonString() ?? "null"));
                if (node is not null)
                {
                    hashOf[node] = leafHash;
                }

                return leafHash;
            }
        }
    }

    /// <summary>
    /// Top-down: for each child of an object/array, replaces it with a "$ref" if its hash is one
    /// of the repeated ones; otherwise recurses into it in place. Never replaces the root itself
    /// (a tool's top-level schema must stay a plain object, not a $ref).
    /// </summary>
    private static void RewriteChildrenInPlace(
        JsonNode container,
        Dictionary<JsonNode, string> hashOf,
        HashSet<string> repeated,
        Dictionary<string, string> canonicalText,
        JsonObject defs,
        Dictionary<string, string> defNameByHash)
    {
        if (container is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var child = obj[key];
                var replacement = TryReplace(child, hashOf, repeated, canonicalText, defs, defNameByHash);
                if (replacement is not null)
                {
                    obj[key] = replacement;
                }
                else if (child is JsonObject or JsonArray)
                {
                    RewriteChildrenInPlace(child!, hashOf, repeated, canonicalText, defs, defNameByHash);
                }
            }
        }
        else if (container is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                var replacement = TryReplace(child, hashOf, repeated, canonicalText, defs, defNameByHash);
                if (replacement is not null)
                {
                    arr[i] = replacement;
                }
                else if (child is JsonObject or JsonArray)
                {
                    RewriteChildrenInPlace(child!, hashOf, repeated, canonicalText, defs, defNameByHash);
                }
            }
        }
    }

    private static JsonNode? TryReplace(
        JsonNode? node,
        Dictionary<JsonNode, string> hashOf,
        HashSet<string> repeated,
        Dictionary<string, string> canonicalText,
        JsonObject defs,
        Dictionary<string, string> defNameByHash)
    {
        if (node is null || !hashOf.TryGetValue(node, out var hash) || !repeated.Contains(hash))
        {
            return null;
        }

        if (!defNameByHash.TryGetValue(hash, out var defName))
        {
            defName = "dedup_" + hash[..12];
            defNameByHash[hash] = defName;

            // Parse a fresh, independent copy — the original instance is still attached at its
            // first occurrence in the document and JsonNode instances can only have one parent.
            var canonicalNode = JsonNode.Parse(canonicalText[hash]);
            if (canonicalNode is JsonObject or JsonArray)
            {
                RewriteChildrenInPlace(canonicalNode!, hashOf, repeated, canonicalText, defs, defNameByHash);
            }

            defs[defName] = canonicalNode;
        }

        return new JsonObject { ["$ref"] = $"#/$defs/{defName}" };
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
