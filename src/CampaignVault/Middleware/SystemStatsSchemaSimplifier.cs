using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// The polymorphic SystemExtension (dnd5e/pf2e/narrative ruleset stats) fully expands its entire
/// anyOf of derived-type schemas everywhere "systemStats" is referenced (character_update,
/// world_build's characters[].systemStats, ...) — roughly 10KB each time, since System.Text.Json's
/// schema exporter has no notion of "advertise a looser shape than what I'll actually bind."
///
/// The real validation lives in SystemStatsMerger, which operates on the deserialized
/// SystemExtension regardless of what schema was advertised — the polymorphic [JsonDerivedType]
/// dispatch on the raw "$system" discriminator happens independent of the tools/list schema. So
/// there's no correctness reason to pay for the full expansion in every tool's advertised schema:
/// this collapses it to a loose object stub and points callers at get_help topic=combat for the
/// per-ruleset field list. Deliberately schema-only — no change to what's actually accepted on the
/// wire, so existing handlers/validation are untouched.
/// </summary>
internal static class SystemStatsSchemaSimplifier
{
    private const string StubDescription =
        "Ruleset-specific stats ($system: dnd5e/pf2e/narrative). See get_help topic=combat for the per-ruleset field list.";

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
                        tool.InputSchema = Simplify(tool.InputSchema);
                    }
                }
                catch
                {
                    // never let a schema-simplification bug break tool discovery — worst case, schema stays as-is.
                }

                try
                {
                    if (tool.OutputSchema is { ValueKind: JsonValueKind.Object } outputSchema)
                    {
                        tool.OutputSchema = Simplify(outputSchema);
                    }
                }
                catch
                {
                    // ignored (same reason as above)
                }
            }

            return result;
        });
    }

    internal static JsonElement Simplify(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText());
        if (root is null)
        {
            return schema;
        }

        var changed = root is JsonObject or JsonArray && SimplifyChildren(root);
        return changed ? JsonSerializer.SerializeToElement(root) : schema;
    }

    /// <summary>Walks the children of container (never replaces the root itself — a schema must stay a plain object).</summary>
    private static bool SimplifyChildren(JsonNode container)
    {
        var changed = false;

        if (container is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonObject childObj && IsSystemExtensionExpansion(childObj))
                {
                    obj[key] = BuildStub(IsNullable(childObj));
                    changed = true;
                }
                else if (child is JsonObject or JsonArray)
                {
                    changed |= SimplifyChildren(child!);
                }
            }
        }
        else if (container is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject or JsonArray)
                {
                    changed |= SimplifyChildren(item!);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Structural signature of SystemExtension's expanded polymorphic schema: an "anyOf" of two or
    /// more branches, at least one of which declares the "$system" discriminator. Matched by shape
    /// rather than by property name/path so it applies wherever this type is referenced, present or future.
    /// </summary>
    private static bool IsSystemExtensionExpansion(JsonObject obj)
    {
        if (obj["anyOf"] is not JsonArray anyOf || anyOf.Count < 2)
        {
            return false;
        }

        return anyOf.Any(branch =>
            branch is JsonObject branchObj &&
            branchObj["properties"] is JsonObject props &&
            props.ContainsKey("$system"));
    }

    private static bool IsNullable(JsonObject obj) =>
        obj["type"] is JsonArray typeArr &&
        typeArr.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "null");

    private static JsonObject BuildStub(bool nullable) => new()
    {
        ["type"] = nullable
            ? new JsonArray(JsonValue.Create("object"), JsonValue.Create("null"))
            : JsonValue.Create("object"),
        ["description"] = StubDescription,
    };
}
