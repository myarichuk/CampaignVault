using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Services;

public static class CreatureQueryBuilder
{
    public const int DefaultPageLimit = 40;
    public const int MaxPageLimit = 100;

    public sealed record CreatureQueryPage(
        IReadOnlyList<CreatureSummaryView> Creatures,
        int TotalCount,
        int Offset,
        int Limit);

    /// <summary>
    /// Query creatures for a given system, merging SRD reference creatures with campaign homebrew.
    /// Homebrew creatures (by name, case-insensitive) override SRD creatures of the same name.
    /// </summary>
    public static async Task<CreatureQueryPage> QueryPageAsync(
        IAsyncDocumentSession session,
        CampaignRepository repository,
        CreatureDefinitionProvider srdProvider,
        string system,
        string? campaignName = null,
        string? nameQuery = null,
        int? levelMin = null,
        int? levelMax = null,
        int offset = 0,
        int? limit = null)
    {
        var pageLimit = Math.Clamp(limit ?? DefaultPageLimit, 1, MaxPageLimit);
        var offsetClamped = Math.Max(0, offset);

        // Fetch SRD creatures (in-memory, sync)
        var srdDict = srdProvider.GetCreaturesForSystem(system);

        // Fetch homebrew creatures (async, from DB)
        var homebrewList = await repository.GetCustomCreaturesForSystemAsync(session, system, campaignName);

        // Merge: build a name-keyed dictionary where homebrew overrides SRD (case-insensitive)
        var merged = new Dictionary<string, (bool isHomebrew, object creature)>(StringComparer.OrdinalIgnoreCase);

        // Add all SRD creatures first
        foreach (var (name, def) in srdDict)
        {
            merged[name] = (false, def);
        }

        // Overlay homebrew creatures (these will override SRD entries with same name)
        foreach (var homebrew in homebrewList)
        {
            if (homebrew.IsArchived)
            {
                continue;
            }

            merged[homebrew.Name] = (true, homebrew);
        }

        // Apply filters (name query, level range) in memory
        var filtered = merged.Values
            .Where(item => MatchesFilters(item.creature, nameQuery, levelMin, levelMax))
            .ToList();

        // Sort by name
        filtered = filtered
            .OrderBy(item => GetCreatureName(item.creature), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = filtered.Count;
        var page = filtered.Skip(offsetClamped).Take(pageLimit).ToList();

        // Convert to view models
        var creaturesView = page.Select(item => ToCreatureSummaryView(item.creature, item.isHomebrew)).ToList();

        return new CreatureQueryPage(creaturesView, totalCount, offsetClamped, pageLimit);
    }

    private static bool MatchesFilters(object creature, string? nameQuery, int? levelMin, int? levelMax)
    {
        var name = GetCreatureName(creature);
        var level = GetCreatureLevel(creature);

        if (!string.IsNullOrWhiteSpace(nameQuery) && !name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
            return false;

        if (levelMin.HasValue && level < levelMin.Value)
            return false;

        if (levelMax.HasValue && level > levelMax.Value)
            return false;

        return true;
    }

    private static string GetCreatureName(object creature) => creature switch
    {
        CreatureDefinition def => def.Name,
        CustomCreature custom => custom.Name,
        _ => string.Empty
    };

    private static int GetCreatureLevel(object creature) => creature switch
    {
        CreatureDefinition def => def.Level ?? 0,
        CustomCreature custom => custom.Level ?? 0,
        _ => 0
    };

    private static CreatureSummaryView ToCreatureSummaryView(object creature, bool isHomebrew) => creature switch
    {
        CreatureDefinition def => new CreatureSummaryView
        {
            Name = def.Name,
            Id = null,
            IsHomebrew = false,
            Level = def.Level,
            ChallengeRating = def.ChallengeRating,
            Hp = def.Hp,
            Defense = def.Defense,
            Description = def.Description,
            Skills = def.Skills,
            Abilities = def.Abilities,
        },
        CustomCreature custom => new CreatureSummaryView
        {
            Name = custom.Name,
            Id = custom.Id,
            IsHomebrew = true,
            Level = custom.Level,
            ChallengeRating = custom.ChallengeRating,
            Hp = custom.Hp,
            Defense = custom.Defense,
            Description = custom.Description,
            Skills = custom.Skills,
            Abilities = custom.Abilities,
        },
        _ => new CreatureSummaryView { Name = string.Empty }
    };

    public static string BuildHint(CreatureQueryPage page)
    {
        if (page.TotalCount == 0)
        {
            return "No creatures found. Try adjusting your filters.";
        }

        if (page.TotalCount <= page.Limit && page.Offset == 0)
        {
            return $"All {page.TotalCount} creature(s) shown.";
        }

        var parts = new List<string>
        {
            $"Showing {page.Creatures.Count} of {page.TotalCount} creature(s)."
        };

        if (page.Offset + page.Limit < page.TotalCount)
        {
            parts.Add($"Call query_creatures with offset={page.Offset + page.Limit} for the next page.");
        }

        return string.Join(" ", parts);
    }

    public static CreatureListResponse ToResponse(
        string system,
        CreatureQueryPage page)
    {
        return new CreatureListResponse
        {
            System = system.ToSlug(),
            Creatures = page.Creatures.ToList(),
            Pagination = new CreatureListPaginationView
            {
                TotalCount = page.TotalCount,
                Offset = page.Offset,
                Limit = page.Limit,
                HasMore = page.Offset + page.Creatures.Count < page.TotalCount
            },
            Hint = BuildHint(page)
        };
    }
}
