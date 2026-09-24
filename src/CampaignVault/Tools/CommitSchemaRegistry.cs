using CampaignVault.Schema;

namespace CampaignVault.Tools;

public record CommitTypeSchema(
    string Type,
    string? Category,
    string Description,
    string[] RequiredFields,
    string[] OptionalFields,
    bool? HasSideEffects,
    string[] SideEffects,
    string[] CoCommitHints,
    string? Example = null
);

internal static class CommitSchemaRegistry
{
    private const int IndexSummaryMaxChars = 60;

    /// <summary>
    /// Type + category + a clipped one-line summary, no fields/side-effects/example — for the unfiltered
    /// call so a caller isn't forced to pay for every variant's full schema just to see what exists.
    /// Engine-only verbs and mode-scoped plugin verbs are omitted; the latter stay resolvable via type=.
    /// </summary>
    public static IReadOnlyList<CommitTypeSchema> GetIndex() =>
        CommitSchemaModel.Variants
            .Where(v => !v.IsEngineOnly && v.ModeId is null)
            .Select(v => new CommitTypeSchema(
                v.Discriminator,
                // Nulls are dropped on the wire: no 'Uncategorized' filler and no hasSideEffects flag
                // (the index doesn't compute side effects, so a constant false was wrong as well as noise).
                v.Category == "Uncategorized" ? null : v.Category,
                ClipSummary(v.Summary), [], [], null, [], []))
            .ToList();

    internal static string ClipSummary(string summary)
    {
        var text = summary.Trim();
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop >= 0)
            text = text[..(stop + 1)];
        if (text.Length <= IndexSummaryMaxChars)
            return text;

        var cut = text.LastIndexOf(' ', IndexSummaryMaxChars);
        return text[..(cut > 0 ? cut : IndexSummaryMaxChars)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    public static IReadOnlyList<CommitTypeSchema> GetAll(string? category = null, string? type = null)
    {
        IEnumerable<CommitVariantModel> variants = CommitSchemaModel.Variants.Where(v => !v.IsEngineOnly);

        // Filter by type if specified
        if (!string.IsNullOrWhiteSpace(type))
        {
            variants = variants.Where(v => v.Discriminator == type).ToList();
        }

        // Filter by category if specified
        if (!string.IsNullOrWhiteSpace(category))
        {
            variants = variants.Where(v => v.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return variants
            .Select(v => new CommitTypeSchema(
                v.Discriminator,
                v.Category,
                v.Summary,
                v.Fields.Where(f => f.IsRequired).Select(f => f.JsonName).ToArray(),
                v.Fields.Where(f => !f.IsRequired).Select(f => f.JsonName).ToArray(),
                v.SideEffects.Count > 0,
                v.SideEffects.ToArray(),
                v.CoCommitHints.ToArray(),
                v.Example
            ))
            .ToList();
    }
}
