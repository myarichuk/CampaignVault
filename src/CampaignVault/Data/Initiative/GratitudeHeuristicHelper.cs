using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

internal static class GratitudeHeuristicHelper
{
    private static readonly HashSet<string> StructuredGratitudeBeats = new(StringComparer.OrdinalIgnoreCase)
    {
        "gratitude", "gift_received", "favor_received"
    };

    private static readonly HashSet<string> GiftItemTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "gift", "reward", "jewelry", "necklace", "present", "heirloom"
    };

    private static readonly HashSet<ItemCategory> GiftCategories =
    [
        ItemCategory.Valuable,
        ItemCategory.Clothing
    ];

    public static bool IsStructuredGratitudeBeat(string? beat) =>
        !string.IsNullOrWhiteSpace(beat) && StructuredGratitudeBeats.Contains(beat);

    public static bool SummaryMatchesHeuristic(string summary, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (Regex.IsMatch(summary, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ItemSuggestsGift(Item item)
    {
        if (item.Tags.Any(t => GiftItemTags.Contains(t)))
        {
            return true;
        }

        return GiftCategories.Contains(item.CoreCategory);
    }
}