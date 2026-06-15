using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

internal static class SceneTravelSummaryExtractor
{
    public static string? GetLastKnownTravel(IEnumerable<Event> events)
    {
        return events
            .FirstOrDefault(e => e.Summary.Contains("travel", StringComparison.OrdinalIgnoreCase)
                              || e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase)
                              || e.Summary.Contains("interrupted", StringComparison.OrdinalIgnoreCase))
            ?.Summary;
    }
}
