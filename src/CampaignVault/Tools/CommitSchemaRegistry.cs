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
    [
        .. CommitSchemaModel.Variants
            .Where(v => !v.IsEngineOnly && v.ModeId is null)
            .Select(v => new CommitTypeSchema(
                v.Discriminator,
                // Nulls are dropped on the wire: no 'Uncategorized' filler and no hasSideEffects flag
                // (the index doesn't compute side effects, so a constant false was wrong as well as noise).
                v.Category == "Uncategorized" ? null : v.Category,
                ClipSummary(v.Summary), [], [], null, [], []))
    ];

    internal static string ClipSummary(string summary) => CommitFieldReference.Clip(summary, IndexSummaryMaxChars);

    public static IReadOnlyList<CommitTypeSchema> GetAll(string? category = null, string? type = null)
    {
        IEnumerable<CommitVariantModel> variants = CommitSchemaModel.Variants.Where(v => !v.IsEngineOnly);

        // Filter by type if specified
        if (!string.IsNullOrWhiteSpace(type))
        {
            variants = [.. variants.Where(v => v.Discriminator == type)];
        }

        // Filter by category if specified
        if (!string.IsNullOrWhiteSpace(category))
        {
            variants = [.. variants.Where(v => v.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase))];
        }

        return
        [
            .. variants
                .Select(v => new CommitTypeSchema(
                    v.Discriminator,
                    v.Category,
                    v.Summary,
                    [.. v.Fields.Where(f => f.IsRequired).Select(FormatField)],
                    [.. v.Fields.Where(f => !f.IsRequired).Select(FormatField)],
                    v.SideEffects.Count > 0,
                    [.. v.SideEffects],
                    [.. v.CoCommitHints],
                    v.Example
                ))
        ];
    }

    // One field per string, in the shared `name:type [req] (enum) {shape} — note` format (CommitFieldReference),
    // so this lookup, the binding errors and the dnd-world-change skill never disagree.
    private static string FormatField(CommitFieldModel field) => CommitFieldReference.FormatField(field);
}
