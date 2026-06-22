using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure;

public static partial class AmbientCrowdHeuristics
{
    private static readonly string[] PromotionBeatKeywords =
    [
        "approach", "approaches", "approached", "steps forward", "stands up", "stood up", "stands with",
        "speaks up", "interrupts", "confronts", "picks up", "picked up", "grabs", "grabbed", "draws", "drew",
        "raises", "calls out", "stumbles toward", "drunken", "drunk", "figure", "someone", "stranger",
        "mercenary", "warrior", "patron", "bartender", "emerges", "breaks away", "separates from the crowd",
        "from the crowd", "in the back", "at the bar", "interrupting"
    ];

    public static int EstimateImpliedCrowdSize(string? ambientCrowd)
    {
        if (string.IsNullOrWhiteSpace(ambientCrowd))
        {
            return 0;
        }

        var numbers = NumberTokenRegex().Matches(ambientCrowd)
            .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

        if (numbers.Count >= 2)
        {
            return (numbers[0] + numbers[^1]) / 2;
        }

        if (numbers.Count == 1)
        {
            return numbers[0];
        }

        if (ambientCrowd.Contains("packed", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("horde", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("mob", StringComparison.OrdinalIgnoreCase))
        {
            return 12;
        }

        if (ambientCrowd.Contains("crowd", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("many", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("several", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("bustling", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        if (ambientCrowd.Contains("few", StringComparison.OrdinalIgnoreCase)
            || ambientCrowd.Contains("couple", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 0;
    }

    public static bool IsCrowdDenseEnough(string? ambientCrowd) =>
        EstimateImpliedCrowdSize(ambientCrowd) >= 3
        || (!string.IsNullOrWhiteSpace(ambientCrowd)
            && ambientCrowd.Contains("crowd", StringComparison.OrdinalIgnoreCase));

    public static bool EventImpliesUnanchoredBeat(Event ev, string locationId)
    {
        if (string.IsNullOrWhiteSpace(ev.Summary))
        {
            return false;
        }

        if (!PromotionBeatKeywords.Any(k => ev.Summary.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (ev.Involved == null || ev.Involved.Count == 0)
        {
            return true;
        }

        if (!ev.Involved.Contains(locationId, StringComparer.OrdinalIgnoreCase)
            && !ev.Involved.Any(id => id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var anchoredNpcCount = ev.Involved.Count(IsCharacterId);
        return anchoredNpcCount <= 1;
    }

    public static bool TryBuildPromotionExample(string locationId, out string exampleJson)
    {
        var id = $"chars/crowd-figure-{Guid.NewGuid().ToString("N")[..6]}";
        exampleJson =
            "[ { \"$type\": \"character_create\", \"characterId\": \"" + id + "\", \"name\": \"Figure from the Crowd\", "
            + "\"currentLocationId\": \"" + locationId + "\", \"currentActivity\": \"Stepping forward from the crowd\", "
            + "\"keepAlive\": false, \"notes\": \"Promoted from ambientCrowd when they became interactable.\" } ]";
        return true;
    }

    public static bool TryBuildAmbientRefreshExample(string locationId, string? currentAmbient, out string exampleJson)
    {
        exampleJson =
            "[ { \"$type\": \"location_update\", \"locationId\": \"" + locationId + "\", "
            + "\"ambientCrowd\": \"Updated crowd mood after time passed (quieter, different faces, etc.)\" } ]";
        return true;
    }

    private static bool IsCharacterId(string id) =>
        id.StartsWith("chars/", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("characters/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberTokenRegex();
}