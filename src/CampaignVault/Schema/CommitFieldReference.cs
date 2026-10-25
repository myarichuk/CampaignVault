using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using CampaignVault.Tools;

namespace CampaignVault.Schema;

/// <summary>
/// The one compact, generated field reference for commit <c>$type</c>s. It feeds
/// <c>lookup kind=commit_schema</c>, the argument-binding error messages and the generated
/// <c>## Fields</c> section of the dnd-world-change skill, so the three never disagree.
/// Per field: <c>name:type [req] (enumA|enumB) — description ≤60 chars</c>; object-typed fields
/// carry their shape one level down (<c>{a:string, b:int}</c>).
/// </summary>
internal static partial class CommitFieldReference
{
    public const int DescriptionMaxChars = 60;
    public const int SkillSectionMaxChars = 4000;
    public const string SkillSectionBegin = "<!-- fields:begin -->";
    public const string SkillSectionEnd = "<!-- fields:end -->";

    /// <summary>Authorable variants: no engine-only verbs, no mode-scoped plugin verbs.</summary>
    public static IEnumerable<CommitVariantModel> Authorable =>
        CommitSchemaModel.Variants.Where(v => !v.IsEngineOnly && v.ModeId is null);

    public static string ValidTypesLine() =>
        "Valid $type values: " + string.Join(", ", Authorable.Select(v => v.Discriminator).OrderBy(d => d, StringComparer.Ordinal)) + ".";

    /// <summary>
    /// One field on one line: <c>name:type [req] (a|b) — desc</c>. With <paramref name="withDescription"/> false
    /// only a conditional-requirement hint survives (the skill section: names and types are what the
    /// stub schema lacks; the prose above it carries the semantics).
    /// </summary>
    public static string FormatField(CommitFieldModel field, bool withDescription = true)
    {
        var sb = new StringBuilder();
        sb.Append(field.JsonName).Append(':').Append(TypeName(field.ClrType));
        if (field.IsRequired)
            sb.Append(" [req]");
        var enumValues = field.EnumValues ?? EnumValuesOf(field.ClrType);
        if (enumValues is { Count: > 0 })
            sb.Append(" (").Append(string.Join("|", enumValues)).Append(')');
        var shape = Shape(field.ClrType);
        if (shape is not null)
            sb.Append(' ').Append(shape);
        var desc = field.RequiredHint ?? (withDescription ? field.Description : null);
        if (!string.IsNullOrWhiteSpace(desc))
            sb.Append(" — ").Append(Clip(desc, DescriptionMaxChars));
        return sb.ToString();
    }

    /// <summary>All fields of a variant, one per line, required first.</summary>
    public static IReadOnlyList<string> FieldLines(CommitVariantModel variant, bool withDescription = true) =>
        [.. variant.Fields.OrderByDescending(f => f.IsRequired).Select(f => FormatField(f, withDescription))];

    /// <summary>
    /// A full block for one type: header line, fields, side effects and co-commit hints.
    /// Used by lookup kind=commit_schema type=X and by binding errors.
    /// </summary>
    public static string FormatVariant(CommitVariantModel variant, bool includeExample = false)
    {
        var sb = new StringBuilder();
        sb.Append("$type ").Append(variant.Discriminator);
        if (variant.Category != "Uncategorized")
            sb.Append(" [").Append(variant.Category).Append(']');
        sb.Append(" — ").Append(Clip(variant.Summary, DescriptionMaxChars));
        foreach (var line in FieldLines(variant))
            sb.Append("\n  ").Append(line);
        if (variant.SideEffects.Count > 0)
            sb.Append("\n  engine also applies: ").Append(string.Join(", ", variant.SideEffects)).Append(" (don't duplicate)");
        if (variant.CoCommitHints.Count > 0)
            sb.Append("\n  pair with: ").Append(string.Join(", ", variant.CoCommitHints));
        if (includeExample && !string.IsNullOrWhiteSpace(variant.Example))
            sb.Append("\n  example: ").Append(variant.Example.Trim());
        return sb.ToString();
    }

    /// <summary>Compact field list for an error message: <c>fields of hp: a:int [req], b:string, …</c>.</summary>
    public static string FieldSummary(CommitVariantModel variant)
    {
        var parts = variant.Fields
            .OrderByDescending(f => f.IsRequired)
            .Select(f =>
            {
                var s = f.JsonName + ":" + TypeName(f.ClrType);
                if (f.IsRequired) s += " [req]";
                var enumValues = f.EnumValues ?? EnumValuesOf(f.ClrType);
                if (enumValues is { Count: > 0 }) s += " (" + string.Join("|", enumValues) + ")";
                return s;
            });
        return $"Fields of {variant.Discriminator}: {string.Join(", ", parts)}.";
    }

    /// <summary>Index line: <c>type — summary</c>.</summary>
    public static string IndexLine(CommitVariantModel variant) =>
        $"{variant.Discriminator} — {Clip(variant.Summary, DescriptionMaxChars)}";

    /// <summary>
    /// The generated skill section: hot-tier types with their fields inline, every other authorable
    /// type as an index line pointing at lookup. Deterministic so a golden-file test can pin it.
    /// </summary>
    public static string RenderSkillSection()
    {
        var sb = new StringBuilder();
        sb.Append(SkillSectionBegin).Append('\n');
        sb.Append("## Fields\n\n");
        sb.Append("Generated from the server: `name:type [req] (enum values) {object shape}`. Unknown field names fail the batch. ")
          .Append("Field notes and every other type: `lookup kind=commit_schema type=<$type>`.\n");
        foreach (var variant in Authorable.Where(v => v.IsHotTier).OrderBy(v => v.Discriminator, StringComparer.Ordinal))
        {
            sb.Append('\n').Append("**").Append(variant.Discriminator).Append("** — ").Append(Clip(variant.Summary, DescriptionMaxChars)).Append('\n');
            foreach (var line in FieldLines(variant, withDescription: false))
                sb.Append("- ").Append(line).Append('\n');
        }

        sb.Append("\nOther types: ");
        sb.Append(string.Join(", ", Authorable.Where(v => !v.IsHotTier).Select(v => v.Discriminator).OrderBy(d => d, StringComparer.Ordinal)));
        sb.Append(".\n");
        sb.Append(SkillSectionEnd).Append('\n');
        return sb.ToString();
    }

    /// <summary>Replaces (or appends) the generated section between the markers in a skill file's text.</summary>
    public static string ApplySkillSection(string skillText)
    {
        var section = RenderSkillSection();
        var begin = skillText.IndexOf(SkillSectionBegin, StringComparison.Ordinal);
        var end = skillText.IndexOf(SkillSectionEnd, StringComparison.Ordinal);
        if (begin >= 0 && end > begin)
        {
            var after = end + SkillSectionEnd.Length;
            if (after < skillText.Length && skillText[after] == '\n') after++;
            return skillText[..begin] + section + skillText[after..];
        }

        var trimmed = skillText.TrimEnd('\n', '\r', ' ');
        return trimmed + "\n\n" + section;
    }

    public static string TypeName(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "int";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
        if (type == typeof(bool)) return "bool";
        if (type.IsEnum) return "enum";
        if (type == typeof(System.Text.Json.JsonElement) || type == typeof(System.Text.Json.Nodes.JsonNode) || type == typeof(object)) return "any";
        if (TryDictionaryValue(type, out var valueType)) return $"map<string,{TypeName(valueType)}>";
        if (TryElementType(type, out var elementType)) return TypeName(elementType) + "[]";
        return "object";
    }

    private static IReadOnlyList<string>? EnumValuesOf(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return type.IsEnum ? Enum.GetNames(type) : null;
    }

    /// <summary>The one-level shape of an object (or object[]) field: <c>{name:type, …}</c>.</summary>
    public static string? Shape(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (TryDictionaryValue(type, out _)) return null;
        if (TryElementType(type, out var element)) type = element;
        if (!IsShapeType(type)) return null;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p =>
            {
                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? CamelCase(p.Name);
                var inner = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var typeName = inner.IsEnum ? "(" + string.Join("|", Enum.GetNames(inner)) + ")" : TypeName(p.PropertyType);
                return name + ":" + typeName;
            })
            .ToList();
        return props.Count == 0 ? null : "{" + string.Join(", ", props) + "}";
    }

    private static bool IsShapeType(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type)
        && type != typeof(System.Text.Json.Nodes.JsonNode) && type != typeof(object);

    private static bool TryDictionaryValue(Type type, out Type valueType)
    {
        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() is var def
                && (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>)))
            {
                valueType = candidate.GetGenericArguments()[1];
                return true;
            }
        }

        valueType = typeof(object);
        return false;
    }

    private static bool TryElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type != typeof(string))
        {
            foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = candidate.GetGenericArguments()[0];
                    return true;
                }
            }
        }

        elementType = typeof(object);
        return false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\((?:e\.g\.|i\.e\.|for example)[^)]*\)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ExampleParenthetical();

    /// <summary>Index of the first sentence-ending period, skipping abbreviations like "e.g." and "i.e.".</summary>
    private static int FirstSentenceEnd(string text)
    {
        var from = 0;
        while (true)
        {
            var stop = text.IndexOf(". ", from, StringComparison.Ordinal);
            if (stop < 0) return -1;
            var isAbbreviation = stop >= 3 && (text.AsSpan(stop - 3, 4) is "e.g." or "i.e." || text.AsSpan(stop - 2, 3) is " g." or " e.");
            if (!isAbbreviation) return stop;
            from = stop + 1;
        }
    }

    private static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>First sentence, cut at a word boundary to <paramref name="max"/> chars.</summary>
    public static string Clip(string text, int max)
    {
        text = ExampleParenthetical().Replace(text.Trim().Replace('\n', ' '), "");
        var stop = FirstSentenceEnd(text);
        if (stop >= 0)
            text = text[..(stop + 1)];
        if (text.Length <= max)
            return text;

        var cut = text.LastIndexOf(' ', max);
        return text[..(cut > 0 ? cut : max)].TrimEnd(',', ';', ':', ' ', '.') + "…";
    }
}
