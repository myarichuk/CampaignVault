using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using CampaignVault.Models.Converters;

namespace CampaignVault.Tools;

/// <summary>
/// Parses commit <c>changes</c> from MCP JSON payloads. The MCP binder cannot reliably
/// materialize polymorphic <see cref="WorldChange"/> arrays directly.
/// </summary>
internal static class CommitChangesParser
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
    };

    public static bool TryParse(JsonElement? changes, out WorldChange[]? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (changes is null)
        {
            return false;
        }

        var el = changes.Value;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        try
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var text = el.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                text = NormalizeEngagementRelationActorId(text);
                text = WorldChangeNormalizer.NormalizeChangesArray(text);
                parsed = JsonSerializer.Deserialize<WorldChange[]>(text, Options);
                return parsed is { Length: > 0 };
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                var text = NormalizeEngagementRelationActorId(el.GetRawText());
                text = WorldChangeNormalizer.NormalizeChangesArray(text);
                parsed = JsonSerializer.Deserialize<WorldChange[]>(text, Options);
                return parsed is { Length: > 0 };
            }

            error = $"Expected 'changes' to be a JSON array, but received {el.ValueKind}.";
            return false;
        }
        catch (JsonException ex)
        {
            var source = el.ValueKind == JsonValueKind.Array ? el : default(JsonElement?);
            error = ModelEnumErrorHints.Enrich(ex, source);
            return false;
        }
    }

    /// <summary>
    /// LLMs occasionally send <c>actorId</c> instead of <c>characterId</c> on
    /// <c>engagement_relation</c> entries (a common naming slip documented as a known
    /// confusion point). Rename it before deserializing so the character isn't silently
    /// dropped — the polymorphic WorldChange deserializer has no alias mechanism of its own.
    /// </summary>
    private static string NormalizeEngagementRelationActorId(string rawArrayJson)
    {
        if (!rawArrayJson.Contains("actorId", StringComparison.Ordinal) ||
            !rawArrayJson.Contains("engagement_relation", StringComparison.Ordinal))
        {
            return rawArrayJson;
        }

        var node = JsonNode.Parse(rawArrayJson);
        if (node is not JsonArray array)
        {
            return rawArrayJson;
        }

        foreach (var entry in array)
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }

            var typeValue = obj.TryGetPropertyValue("$type", out var typeNode) ? typeNode?.GetValue<string>() : null;
            if (typeValue != "engagement_relation")
            {
                continue;
            }

            if (!obj.ContainsKey("characterId") && obj.TryGetPropertyValue("actorId", out var actorIdNode))
            {
                obj.Remove("actorId");
                obj["characterId"] = actorIdNode?.DeepClone();
            }
        }

        return array.ToJsonString();
    }
}