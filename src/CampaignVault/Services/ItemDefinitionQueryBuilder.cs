using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

public static class ItemDefinitionQueryBuilder
{
    public const int DefaultPageLimit = 40;
    public const int MaxPageLimit = 100;

    public sealed record ItemDefinitionQueryPage(
        IReadOnlyList<ItemDefinition> Items,
        int TotalCount,
        int Offset,
        int Limit);

    public static ItemDefinitionQueryPage QueryPage(
        ItemDefinitionProvider provider,
        string system,
        string? nameQuery = null,
        string? category = null,
        string? tag = null,
        int offset = 0,
        int? limit = null)
    {
        var pageLimit = Math.Clamp(limit ?? DefaultPageLimit, 1, MaxPageLimit);
        var offsetClamped = Math.Max(0, offset);

        var all = provider.QueryItems(system, nameQuery, category, tag);
        var page = all.Skip(offsetClamped).Take(pageLimit).ToList();

        return new ItemDefinitionQueryPage(page, all.Count, offsetClamped, pageLimit);
    }

    public static string BuildHint(ItemDefinitionQueryPage page)
    {
        if (page.TotalCount == 0)
        {
            return "No items found. Try adjusting your filters or verify the system's items pack is loaded.";
        }

        if (page.TotalCount <= page.Limit && page.Offset == 0)
        {
            return $"All {page.TotalCount} item(s) shown.";
        }

        var parts = new List<string>
        {
            $"Showing {page.Items.Count} of {page.TotalCount} item(s)."
        };

        if (page.Offset + page.Limit < page.TotalCount)
        {
            parts.Add($"Call lookup with kind:'items', offset={page.Offset + page.Limit} for the next page.");
        }

        return string.Join(" ", parts);
    }

    public static ItemDefinitionListResponse ToResponse(
        string system,
        ItemDefinitionQueryPage page)
    {
        return new ItemDefinitionListResponse
        {
            System = system.ToSlug(),
            Items = page.Items.Select(ToSummary).ToList(),
            Pagination = new ItemDefinitionListPaginationView
            {
                TotalCount = page.TotalCount,
                Offset = page.Offset,
                Limit = page.Limit,
                HasMore = page.Offset + page.Items.Count < page.TotalCount
            },
            Hint = BuildHint(page)
        };
    }

    private static ItemDefinitionSummaryView ToSummary(ItemDefinition item) =>
        new()
        {
            Name = item.Name,
            Category = item.Category?.ToString(),
            Tags = item.Tags,
            Description = item.Description,
            Properties = item.Properties
        };
}
