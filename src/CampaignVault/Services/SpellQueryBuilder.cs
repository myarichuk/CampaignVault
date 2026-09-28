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
        string system,
        string? className,
        ClassDefinitionProvider classProvider,
        int? level = null,
        int offset = 0,
        int? limit = null,
        IReadOnlyList<CustomSpell>? homebrew = null,
        string? nameQuery = null)
    {
        var pageLimit = Math.Clamp(limit ?? DefaultPageLimit, 1, MaxPageLimit);
        var offsetClamped = Math.Max(0, offset);

        var srd = provider.QuerySpellsRanked(system, className, level, classProvider, nameQuery);

        // Merge: homebrew overrides SRD entries of the same name (case-insensitive).
        var merged = new Dictionary<string, NameSearchHit<SpellDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in srd)
        {
            merged[hit.Item.Name] = hit;
        }

        if (homebrew != null)
        {
            var homebrewScores = string.IsNullOrWhiteSpace(nameQuery)
                ? null
                : NameSearchIndex<CustomSpell>.Build(homebrew, c => c.Name).Search(nameQuery)
                    .ToDictionary(h => h.Item, h => h.Score);

            foreach (var custom in homebrew)
            {
                if (custom.IsArchived)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(className)
                    && !custom.Classes.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var score = 0;
                if (homebrewScores != null && !homebrewScores.TryGetValue(custom, out score))
                {
                    continue;
                }

                if (level.HasValue && custom.Level != level.Value)
                {
                    continue;
                }

                merged[custom.Name] = new NameSearchHit<SpellDefinition>(new SpellDefinition
                {
                    Name = custom.Name,
                    System = system.ToSlug(),
                    Description = custom.Description,
                    Level = custom.Level,
                    Classes = custom.Classes,
                    Concentration = custom.Concentration,
                    CastingTime = custom.CastingTime,
                }, score);
            }
        }

        // Name search: best match first. Otherwise alphabetical.
        var all = (string.IsNullOrWhiteSpace(nameQuery)
                ? merged.Values.OrderBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase)
                : merged.Values.OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase))
            .Select(h => h.Item)
            .ToList();
        var page = all.Skip(offsetClamped).Take(pageLimit).ToList();

        return new SpellQueryPage(page, all.Count, offsetClamped, pageLimit);
    }

    public static string BuildHint(SpellQueryPage page, string? className, int? level, string? nameQuery = null)
    {
        var scope = string.IsNullOrWhiteSpace(className) ? "any class" : className;
        var filters = string.IsNullOrWhiteSpace(nameQuery) ? "" : $" matching '{nameQuery}'";
        var levelNote = level.HasValue ? $" at level {level.Value}" : string.Empty;

        if (page.TotalCount == 0)
        {
            return string.IsNullOrWhiteSpace(nameQuery)
                ? $"No spells for {scope}{levelNote}. Try another level or verify class name via lookup kind:'handbook'."
                : $"No spells{filters} for {scope}{levelNote}. Try fewer words, drop the level/className filter, or check the spelling.";
        }

        if (page.TotalCount <= page.Limit && page.Offset == 0)
        {
            return $"All {page.TotalCount} spell(s){filters} for {scope}{levelNote} shown.";
        }

        var parts = new List<string>
        {
            $"Showing {page.Spells.Count} of {page.TotalCount} spell(s){filters} for {scope}{levelNote}."
        };

        if (page.Offset + page.Limit < page.TotalCount)
        {
            parts.Add($"Call lookup with kind:'spells', offset={page.Offset + page.Limit} for the next page.");
        }

        if (!level.HasValue && string.IsNullOrWhiteSpace(nameQuery) && page.TotalCount > DefaultPageLimit)
        {
            parts.Add("Prefer level=0..9 or query=<name> to narrow results before paging.");
        }

        return string.Join(" ", parts);
    }

    public static SpellListResponse ToResponse(
        string system,
        string? className,
        int? level,
        SpellQueryPage page,
        Func<SpellDefinition, SpellSummaryView> toSummary,
        string? nameQuery = null)
    {
        return new SpellListResponse
        {
            System = system.ToSlug(),
            Class = className,
            FilterLevel = level,
            Spells = [.. page.Spells.Select(toSummary)],
            Pagination = new SpellListPaginationView
            {
                TotalCount = page.TotalCount,
                Offset = page.Offset,
                Limit = page.Limit,
                HasMore = page.Offset + page.Spells.Count < page.TotalCount
            },
            Hint = BuildHint(page, className, level, nameQuery)
        };
    }
}