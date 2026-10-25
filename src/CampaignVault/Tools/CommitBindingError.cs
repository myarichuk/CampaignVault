using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Models;
using CampaignVault.Schema;

namespace CampaignVault.Tools;

/// <summary>
/// Turns a <see cref="JsonException"/> from binding commit <c>changes</c> into one short message the
/// model can act on: the failing path, the <c>$type</c> there, and either that type's compact field
/// list, the valid <c>$type</c> list, or the enum/number hint. Capped at <see cref="MaxChars"/>.
/// </summary>
internal static partial class CommitBindingError
{
    public const int MaxChars = 600;
    public const string MissingTypeMarker = "is missing $type";

    [GeneratedRegex(@"The JSON property '([^']+)' could not be mapped to any \.NET member contained in type '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownMemberRegex();

    [GeneratedRegex(@"type discriminator (?:id )?'([^']+)'", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnknownDiscriminatorRegex();

    [GeneratedRegex(@"Path:\s*(\$[^\s|]*)", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\s*\|\s*LineNumber:\s*\d+\s*\|\s*BytePositionInLine:\s*\d+\.?", RegexOptions.CultureInvariant)]
    private static partial Regex PositionNoiseRegex();

    [GeneratedRegex(@"\$\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex IndexRegex();

    /// <param name="ex">The binding failure.</param>
    /// <param name="changes">The <c>changes</c> array as sent, when available (paths are relative to it).</param>
    public static string Describe(JsonException ex, JsonElement? changes)
    {
        var message = ex.Message;
        var path = !string.IsNullOrWhiteSpace(ex.Path) ? ex.Path : PathRegex().Match(message).Groups[1].Value;
        var variant = VariantAtPath(path, changes);

        if (message.Contains(MissingTypeMarker, StringComparison.Ordinal))
        {
            return Clip($"{StripNoise(message)} {CommitFieldReference.ValidTypesLine()}");
        }

        var unknownMember = UnknownMemberRegex().Match(message);
        if (unknownMember.Success)
        {
            var field = unknownMember.Groups[1].Value;
            var clrName = unknownMember.Groups[2].Value;
            variant = CommitSchemaModel.Variants.FirstOrDefault(v => v.ClrType.FullName == clrName) ?? variant;
            var sb = new StringBuilder();
            sb.Append("Unknown field '").Append(field).Append('\'');
            if (!string.IsNullOrWhiteSpace(path)) sb.Append(" at ").Append(path);
            if (variant is not null)
            {
                sb.Append(" ($type ").Append(variant.Discriminator).Append(')');
                var suggestion = Suggest(field, variant.Fields.Select(f => f.JsonName));
                if (suggestion is not null) sb.Append(". Did you mean '").Append(suggestion).Append('\'');
                sb.Append(". ").Append(CommitFieldReference.FieldSummary(variant));
            }
            else
            {
                sb.Append(" (").Append(clrName.Split('.').Last()).Append("). The batch was not applied; remove or rename it.");
            }

            return Clip(sb.ToString());
        }

        var unknownType = UnknownDiscriminatorRegex().Match(message);
        if (unknownType.Success)
        {
            var at = string.IsNullOrWhiteSpace(path) ? "" : $" at {path}";
            return Clip($"Unknown $type '{unknownType.Groups[1].Value}'{at}. {CommitFieldReference.ValidTypesLine()}");
        }

        // Enum / number mismatches: the enum hint carries the bad value and the valid list.
        var enriched = StripNoise(ModelEnumErrorHints.Enrich(ex, changes));
        if (variant is not null)
        {
            var summary = CommitFieldReference.FieldSummary(variant);
            if (enriched.Length + summary.Length + 1 <= MaxChars)
                enriched = $"{enriched} {summary}";
        }

        return Clip(enriched);
    }

    private static CommitVariantModel? VariantAtPath(string? path, JsonElement? changes)
    {
        if (changes is not { ValueKind: JsonValueKind.Array } root || string.IsNullOrWhiteSpace(path))
            return null;

        var index = IndexRegex().Match(path);
        if (!index.Success || !int.TryParse(index.Groups[1].Value, out var i) || i < 0 || i >= root.GetArrayLength())
            return null;

        var item = root[i];
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("$type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
            return null;

        return CommitSchemaModel.Find(typeNode.GetString() ?? "");
    }

    /// <summary>Case-insensitive match, then prefix/containment, then an edit distance of at most 2.</summary>
    internal static string? Suggest(string field, IEnumerable<string> candidates)
    {
        var list = candidates.ToList();
        var exact = list.FirstOrDefault(c => string.Equals(c, field, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var contains = list.FirstOrDefault(c =>
            c.Contains(field, StringComparison.OrdinalIgnoreCase) || field.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (contains is not null && Math.Abs(contains.Length - field.Length) <= 4) return contains;

        return list
            .Select(c => (name: c, d: Distance(field.ToLowerInvariant(), c.ToLowerInvariant())))
            .Where(x => x.d <= 2)
            .OrderBy(x => x.d)
            .Select(x => x.name)
            .FirstOrDefault();
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private static string StripNoise(string message) => PositionNoiseRegex().Replace(message, "").Trim();

    private static string Clip(string text)
    {
        if (text.Length <= MaxChars) return text;
        var cut = text.LastIndexOf(' ', MaxChars - 1);
        return text[..(cut > MaxChars / 2 ? cut : MaxChars - 1)].TrimEnd(',', ' ') + "…";
    }
}
