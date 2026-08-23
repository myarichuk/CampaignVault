using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Turns raw System.Text.Json enum failures into LLM-actionable hints (valid values, common aliases).
/// Scans every enum in <c>CampaignVault.Models</c>, so this applies to any tool's argument
/// binding failure (commit's polymorphic changes, upsert requests, etc.), not just commit.
/// </summary>
internal static partial class ModelEnumErrorHints
{
    private static readonly IReadOnlyDictionary<string, Type> EnumTypesByName = BuildEnumLookup();

    // System.Text.Json renders nullable enum fields as "System.Nullable`1[Namespace.Type]" —
    // match that wrapper first (capturing the inner type), falling back to a bare type name.
    [GeneratedRegex(@"could not be converted to (?:System\.Nullable`1\[([\w\.]+)\]|([\w\.]+?)(?=\.\s|\s|$))", RegexOptions.CultureInvariant)]
    private static partial Regex EnumTypeRegex();

    [GeneratedRegex(@"Path:\s*(\$\[[^\]]+\](?:\.[\w]+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPathRegex();

    public static string Enrich(JsonException ex, JsonElement? source = null)
    {
        var message = ex.Message;
        var path = string.IsNullOrWhiteSpace(ex.Path)
            ? JsonPathRegex().Match(message).Groups[1].Value
            : ex.Path;

        var typeMatch = EnumTypeRegex().Match(message);
        if (!typeMatch.Success)
        {
            return message;
        }

        var typeName = (typeMatch.Groups[1].Success ? typeMatch.Groups[1].Value : typeMatch.Groups[2].Value)
            .TrimEnd('.');
        if (!TryResolveEnumType(typeName, out var enumType)
            && !TryResolveEnumFromPath(path, source, out enumType))
        {
            return TryEnrichNumericMismatch(message, typeName, path, source);
        }

        var validList = string.Join(", ", Enum.GetNames(enumType));

        string? badValue = null;
        if (source is { } root && !string.IsNullOrWhiteSpace(path))
        {
            badValue = TryReadValueAtPath(root, path);
        }

        var hint = badValue is null
            ? $"Valid values for {enumType.Name}: {validList}."
            : $"Got '{badValue}' for {enumType.Name}. Valid values: {validList}.";

        var alias = TrySuggestAlias(enumType, badValue);
        if (alias is not null)
        {
            hint += $" Did you mean '{alias}'?";
        }

        return $"{message} {hint}";
    }

    private static readonly IReadOnlyDictionary<string, string> PrimitiveTypeHints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["System.Double"] = "Expected a JSON number (e.g. 0.75), not a word or quoted string.",
        ["System.Single"] = "Expected a JSON number (e.g. 0.75), not a word or quoted string.",
        ["System.Decimal"] = "Expected a JSON number (e.g. 0.75), not a word or quoted string.",
        ["System.Int32"] = "Expected a JSON integer (e.g. 3), not a word or quoted string.",
        ["System.Int64"] = "Expected a JSON integer (e.g. 3), not a word or quoted string.",
        ["System.Boolean"] = "Expected a JSON boolean (true/false), not a quoted string.",
    };

    // Not an enum mismatch (e.g. `salience` sent as a word like "High" instead of a number) —
    // give a concrete "expected a <type>" hint instead of the raw, unexplained CLR message.
    private static string TryEnrichNumericMismatch(string message, string typeName, string? path, JsonElement? source)
    {
        if (!PrimitiveTypeHints.TryGetValue(typeName, out var expectedHint))
        {
            return message;
        }

        var badValue = source is { } root && !string.IsNullOrWhiteSpace(path)
            ? TryReadValueAtPath(root, path)
            : null;

        var hint = badValue is null ? expectedHint : $"Got '{badValue}'. {expectedHint}";

        return $"{message} {hint}";
    }

    private static IReadOnlyDictionary<string, Type> BuildEnumLookup()
    {
        var lookup = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in typeof(WorldChange).Assembly.GetTypes())
        {
            if (!type.IsEnum || type.Namespace != "CampaignVault.Models")
            {
                continue;
            }

            lookup[type.Name] = type;
            if (type.FullName is not null)
            {
                lookup[type.FullName] = type;
            }
        }

        return lookup;
    }

    private static bool TryResolveEnumFromPath(string? path, JsonElement? source, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? enumType)
    {
        enumType = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.EndsWith(".category", StringComparison.OrdinalIgnoreCase))
        {
            enumType = typeof(EventCategory);
            return true;
        }

        if (path.EndsWith(".newState", StringComparison.OrdinalIgnoreCase))
        {
            if (source is { } rootNode
                && TryReadChangeTypeAtPath(rootNode, path, out var ct)
                && string.Equals(ct, "quest_progress", StringComparison.OrdinalIgnoreCase))
            {
                enumType = typeof(QuestState);
            }
            else
            {
                enumType = typeof(RumorState);
            }
            return true;
        }

        if (path.EndsWith(".actionType", StringComparison.OrdinalIgnoreCase))
        {
            enumType = typeof(RulesetActionType);
            return true;
        }

        return false;
    }

    private static bool TryReadChangeTypeAtPath(JsonElement root, string path, out string? changeType)
    {
        changeType = null;
        var bracketStart = path.IndexOf('[');
        if (bracketStart < 0)
        {
            return false;
        }

        var bracketEnd = path.IndexOf(']', bracketStart);
        if (bracketEnd < 0)
        {
            return false;
        }

        var indexText = path[(bracketStart + 1)..bracketEnd];
        if (!int.TryParse(indexText, out var index)
            || root.ValueKind != JsonValueKind.Array
            || index < 0
            || index >= root.GetArrayLength())
        {
            return false;
        }

        var item = root[index];
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("$type", out var typeNode)
            && typeNode.ValueKind == JsonValueKind.String)
        {
            changeType = typeNode.GetString();
            return changeType is not null;
        }

        return false;
    }

    private static bool TryResolveEnumType(string typeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? enumType)
    {
        if (EnumTypesByName.TryGetValue(typeName, out var typeValue))
        {
            enumType = typeValue;
            return true;
        }

        var shortName = typeName.Split('.').LastOrDefault();
        if (shortName is not null && EnumTypesByName.TryGetValue(shortName, out typeValue))
        {
            enumType = typeValue;
            return true;
        }

        enumType = null;
        return false;
    }

    private static string? TryReadValueAtPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Bare "$" is the root marker on an object-rooted path (e.g. "$.type" for a single
            // upsert entity payload, as opposed to commit's array-rooted "$[0].newState").
            if (segment == "$")
            {
                continue;
            }

            if (TryNavigateArraySegment(segment, ref current))
            {
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool TryNavigateArraySegment(string segment, ref JsonElement current)
    {
        var bracketStart = segment.IndexOf('[');
        if (bracketStart < 0 || !segment.EndsWith(']'))
        {
            return false;
        }

        var indexText = segment[(bracketStart + 1)..^1];
        if (!int.TryParse(indexText, out var index)
            || current.ValueKind != JsonValueKind.Array
            || index < 0
            || index >= current.GetArrayLength())
        {
            return false;
        }

        current = current[index];
        return true;
    }

    private static string? TrySuggestAlias(Type enumType, string? badValue)
    {
        if (string.IsNullOrWhiteSpace(badValue))
        {
            return null;
        }

        return (enumType.Name, badValue.Trim()) switch
        {
            ("LocationType", "City" or "city" or "Town" or "town") => nameof(LocationType.Settlement),
            ("LocationType", "Tavern" or "tavern" or "Inn" or "inn" or "Shop" or "shop") => nameof(
                LocationType.Building),
            ("EventCategory", "Narrative" or "narrative" or "Roleplay" or "roleplay") => nameof(EventCategory
                .Conversation),
            ("EventCategory", "Scene" or "scene") => nameof(EventCategory.Interaction),
            ("RumorState", "Active" or "active") => nameof(RumorState.Nascent),
            ("RulesetActionType", "Meta" or "meta") => nameof(RulesetActionType.SkillCheck),
            _ => null,
        };
    }
}