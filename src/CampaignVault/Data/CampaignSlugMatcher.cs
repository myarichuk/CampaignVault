using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Fuzzy slug matching for campaign slug suggestions.
/// </summary>
public static class CampaignSlugMatcher
{
    private const int MaxSuggestions = 3;
    private const int MaxLevenshteinDistance = 2;

    public static IReadOnlyList<CampaignSuggestion> FindSuggestions(
        string requestedSlug,
        IReadOnlyList<Campaign> campaigns,
        Func<Campaign, CampaignSuggestion> toSuggestion)
    {
        if (campaigns.Count == 0)
        {
            return [];
        }

        var scored = campaigns
            .Select(c => (Suggestion: toSuggestion(c), Score: Score(requestedSlug, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Suggestion.Slug, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(x => x.Suggestion)
            .ToList();

        return scored;
    }

    private static int Score(string requestedSlug, Campaign campaign)
    {
        var candidateSlug = CampaignSlug.TryCanonicalize(campaign.Name, out var slug)
            ? slug
            : campaign.Name;

        if (string.Equals(requestedSlug, candidateSlug, StringComparison.Ordinal))
        {
            return 0;
        }

        var compactRequested = CompactSlug(requestedSlug);
        var compactCandidate = CompactSlug(candidateSlug);

        if (string.Equals(compactRequested, compactCandidate, StringComparison.Ordinal))
        {
            return 95;
        }

        if (candidateSlug.Contains(requestedSlug, StringComparison.Ordinal)
            || requestedSlug.Contains(candidateSlug, StringComparison.Ordinal)
            || compactCandidate.Contains(compactRequested, StringComparison.Ordinal)
            || compactRequested.Contains(compactCandidate, StringComparison.Ordinal))
        {
            return 90;
        }

        if (candidateSlug.StartsWith(requestedSlug, StringComparison.Ordinal)
            || requestedSlug.StartsWith(candidateSlug, StringComparison.Ordinal)
            || compactCandidate.StartsWith(compactRequested, StringComparison.Ordinal)
            || compactRequested.StartsWith(compactCandidate, StringComparison.Ordinal))
        {
            return 80;
        }

        var distance = LevenshteinDistance(compactRequested, compactCandidate);
        if (distance <= MaxLevenshteinDistance)
        {
            return 70 - distance * 10;
        }

        var display = campaign.DisplayName ?? campaign.Name;
        if (display.Contains(requestedSlug, StringComparison.OrdinalIgnoreCase)
            || requestedSlug.Contains(display, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return 0;
    }

    private static string CompactSlug(string slug) =>
        slug.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}