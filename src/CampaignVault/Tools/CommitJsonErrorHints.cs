using CampaignVault.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CampaignVault.Tools;

/// <summary>
/// Turns raw System.Text.Json enum failures into LLM-actionable hints (valid values, common aliases).
/// </summary>
internal static partial class CommitJsonErrorHints
{
    private static readonly IReadOnlyDictionary<string, Type> EnumTypesByName = BuildEnumLookup();

    [GeneratedRegex(@"could not be converted to ([\w\.]+?)(?=\.\s|\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EnumTypeRegex();

    [GeneratedRegex(@"Path:\s*(\$\[[^\]]+\](?:\.[\w]+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPathRegex();

    public static string Enrich(JsonException ex, JsonElement? source = null)
    {
        var message = ex.Message;
        var path = string.IsNullOrWhiteSpace(ex.Path)
            ? JsonPathRegex().Match(message).Groups[1].Value
            : ex.Path;

        if (!EnumTypeRegex().IsMatch(message))
        {
            return message;
        }

        var typeName = EnumTypeRegex().Match(message).Groups[1].Value.TrimEnd('.');
        if (!TryResolveEnumType(typeName, out var enumType)
            && !TryResolveEnumFromPath(path, source, out enumType))
        {
            return message;
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

    private static bool TryResolveEnumFromPath(string? path, JsonElement? source, out Type enumType)
    {
        enumType = null!;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.EndsWith(".category", StringComparison.OrdinalIgnoreCase))
        {
            enumType = typeof(EventCategory);
            return true;
        }

        if (path.EndsWith(".type", StringComparison.OrdinalIgnoreCase)
            && source is { } root
            && TryReadChangeTypeAtPath(root, path, out var changeType)
            && string.Equals(changeType, "location_create", StringComparison.OrdinalIgnoreCase))
        {
            enumType = typeof(LocationType);
            return true;
        }

        if (path.EndsWith(".newState", StringComparison.OrdinalIgnoreCase))
        {
            enumType = typeof(RumorState);
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

    private static bool TryResolveEnumType(string typeName, out Type enumType)
    {
        if (EnumTypesByName.TryGetValue(typeName, out enumType!))
        {
            return true;
        }

        var shortName = typeName.Split('.').LastOrDefault();
        if (shortName is not null && EnumTypesByName.TryGetValue(shortName, out enumType!))
        {
            return true;
        }

        enumType = null!;
        return false;
    }

    private static string? TryReadValueAtPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
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
            ("LocationType", "Tavern" or "tavern" or "Inn" or "inn" or "Shop" or "shop") => nameof(LocationType.Building),
            ("EventCategory", "Narrative" or "narrative" or "Roleplay" or "roleplay") => nameof(EventCategory.Conversation),
            ("EventCategory", "Scene" or "scene") => nameof(EventCategory.Interaction),
            _ => null,
        };
    }
}