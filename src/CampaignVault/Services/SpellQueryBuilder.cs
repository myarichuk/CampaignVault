using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

public static class SpellQueryBuilder
{
    public const int DefaultPageLimit = 40;
    public const int MaxPageLimit = 100;

    public sealed record SpellQueryPage(
        IReadOnlyList<SpellDefinition> Spells,
        int TotalCount,
        int Offset,
        int Limit);

    public static SpellQueryPage QueryPage(
        SpellDefinitionProvider provider,
        RulesetSystem system,
        string className,
        ClassDefinitionProvider classProvider,
        int? level = null,
        int offset = 0,
        int? limit = null)
    {
        var pageLimit = Math.Clamp(limit ?? DefaultPageLimit, 1, MaxPageLimit);
        var offsetClamped = Math.Max(0, offset);

        var all = provider.QuerySpells(system, className, level, classProvider);
        var page = all.Skip(offsetClamped).Take(pageLimit).ToList();

        return new SpellQueryPage(page, all.Count, offsetClamped, pageLimit);
    }

    public static string BuildHint(SpellQueryPage page, string className, int? level)
    {
        if (page.TotalCount == 0)
        {
            return level.HasValue
                ? $"No spells for {className} at level {level.Value}. Try another level or verify class name via get_system_handbook."
                : $"No spells for {className}. Verify class name via get_system_handbook.";
        }

        if (page.TotalCount <= page.Limit && page.Offset == 0)
        {
            var levelNote = level.HasValue ? $" at level {level.Value}" : string.Empty;
            return $"All {page.TotalCount} spell(s) for {className}{levelNote} shown.";
        }

        var parts = new List<string>
        {
            $"Showing {page.Spells.Count} of {page.TotalCount} spell(s) for {className}"
        };

        if (level.HasValue)
        {
            parts[0] += $" at level {level.Value}";
        }

        parts[0] += ".";

        if (page.Offset + page.Limit < page.TotalCount)
        {
            parts.Add($"Call get_spells with offset={page.Offset + page.Limit} for the next page.");
        }

        if (!level.HasValue && page.TotalCount > DefaultPageLimit)
        {
            parts.Add("Prefer level=0..9 filter to narrow results before paging.");
        }

        return string.Join(" ", parts);
    }

    public static SpellListResponse ToResponse(
        RulesetSystem system,
        string className,
        int? level,
        SpellQueryPage page,
        Func<SpellDefinition, SpellSummaryView> toSummary)
    {
        return new SpellListResponse
        {
            System = system.ToSlug(),
            Class = className,
            FilterLevel = level,
            Spells = page.Spells.Select(toSummary).ToList(),
            Pagination = new SpellListPaginationView
            {
                TotalCount = page.TotalCount,
                Offset = page.Offset,
                Limit = page.Limit,
                HasMore = page.Offset + page.Spells.Count < page.TotalCount
            },
            Hint = BuildHint(page, className, level)
        };
    }
}