using CampaignVault.Schema;

namespace CampaignVault.Tools;

public record CommitTypeSchema(
    string Type,
    string Category,
    string Description,
    string[] RequiredFields,
    string[] OptionalFields,
    bool HasSideEffects,
    string[] SideEffects,
    string[] CoCommitHints,
    string? Example = null
);

internal static class CommitSchemaRegistry
{
    /// <summary>
    /// Type + category + one-line summary only, no fields/side-effects/example — for the unfiltered
    /// call so a caller isn't forced to pay for every variant's full schema just to see what exists.
    /// </summary>
    public static IReadOnlyList<CommitTypeSchema> GetIndex() =>
        CommitSchemaModel.Variants
            .Select(v => new CommitTypeSchema(v.Discriminator, v.Category, v.Summary, [], [], false, [], []))
            .ToList();

    public static IReadOnlyList<CommitTypeSchema> GetAll(string? category = null, string? type = null)
    {
        var variants = CommitSchemaModel.Variants;

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
