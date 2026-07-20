using CampaignVault.Models;

namespace CampaignVault.Data.Pressure;

/// <summary>
/// Helpers for materializing Points of Interest.
/// The LLM decides when a PoI (item, entrance, wall feature, notice board, magical marking, etc.)
/// has been interacted with in a way that should become recallable persistent state
/// (e.g. via glow spell revealing runes, firebolt leaving a scorch mark, reading posters, etc.).
/// We only provide the tooling and gentle suggestions; no verb keyword heuristics.
/// </summary>
public static class PointOfInterestHeuristics
{
    /// <summary>
    /// Builds a ready-to-paste commit example to materialize a specific PoI.
    /// The example includes location_update + a knowledge_update (the examiner should be filled in by the caller).
    /// It also suggests related creates (e.g. sub-location) when the PoI name suggests an entrance.
    /// </summary>
    public static bool TryBuildMaterializeExample(string locationId, string poiName, out string exampleJson)
    {
        var normalizedPoi = poiName;
        var lower = poiName.ToLowerInvariant();

        string detailsPlaceholder = "The specific state or contents after the interaction (LLM fills in what was revealed, changed, or discovered).";
        string extra = "";

        if (lower.Contains("door") || lower.Contains("exit") || lower.Contains("stairs") || lower.Contains("passage") || lower.Contains("archway") || lower.Contains("street") || lower.Contains("alley") || lower.Contains("path"))
        {
            var targetId = $"locations/{Guid.NewGuid().ToString("N")[..8]}";
            detailsPlaceholder = "Detailed description of what lies beyond or the revealed path.";
            extra = $"\nIf this reveals a new connected area, call world_build separately: " +
                     $"{{ \"locations\": [ {{ \"id\": \"{targetId}\", \"name\": \"Revealed or connected area\", \"description\": \"...\", \"type\": \"Room\", \"connectedFromLocationId\": \"{locationId}\", \"connectionDescription\": \"{normalizedPoi}\" }} ] }}";
        }
        else if (lower.Contains("board") || lower.Contains("poster") || lower.Contains("notice") || lower.Contains("sign") || lower.Contains("job"))
        {
            detailsPlaceholder = "Key information read from the board (wanted posters, job offers, notes, etc.).";
        }
        else if (lower.Contains("scorch") || lower.Contains("mark") || lower.Contains("rune") || lower.Contains("symbol") || lower.Contains("glyph"))
        {
            detailsPlaceholder = "The persistent mark or revealed detail (e.g. 'charred glyph left by firebolt', 'glowing runes visible only under light spell').";
        }

        exampleJson =
            "[ " +
            "{ \"$type\": \"location_update\", \"locationId\": \"" + locationId + "\", " +
            "\"materializePointOfInterest\": \"" + normalizedPoi.Replace("\"", "\\\"") + "\", " +
            "\"poiDetails\": \"" + detailsPlaceholder + "\" }, " +
            "{ \"$type\": \"knowledge_update\", \"characterId\": \"chars/REPLACE_WITH_EXAMINER_ID\", \"topic\": \"" + normalizedPoi.Replace("\"", "\\\"") + "\", " +
            "\"details\": \"" + detailsPlaceholder + "\", \"source\": \"Observed\", \"importance\": \"Important\" }" +
            " ]" +
            extra;
        return true;
    }

    public static bool PoiHasDetails(Location loc, string poiName)
    {
        if (loc.PointOfInterestDetails == null || loc.PointOfInterestDetails.Count == 0)
            return false;

        foreach (var kv in loc.PointOfInterestDetails)
        {
            if (string.Equals(kv.Key, poiName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the list of PoI names that currently have no materialized details.
    /// </summary>
    public static List<string> GetUnmaterializedPois(Location loc)
    {
        var pois = loc.PointsOfInterest ?? new List<string>();
        return pois
            .Where(p => !string.IsNullOrWhiteSpace(p) && !PoiHasDetails(loc, p))
            .ToList();
    }
}
