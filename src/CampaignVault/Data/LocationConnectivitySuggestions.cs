using System.Text.Json.Nodes;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Builds structured commit suggestions for location connectivity repairs.
/// </summary>
internal static class LocationConnectivitySuggestions
{
    public static string BuildReverseExitCommitJson(string targetLocationId, string sourceLocationId, string sourceName)
    {
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["$type"] = "location_update",
                ["locationId"] = targetLocationId,
                ["addExit"] = new JsonObject
                {
                    ["targetLocationId"] = sourceLocationId,
                    ["description"] = $"Leads back to {sourceName}"
                }
            }
        };
        return arr.ToJsonString();
    }

    public static string BuildNoExitsCommitJson(string locationId)
    {
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["$type"] = "location_update",
                ["locationId"] = locationId,
                ["addExit"] = new JsonObject
                {
                    ["targetLocationId"] = "locations/previous_area",
                    ["description"] = "Return path"
                }
            }
        };
        return arr.ToJsonString();
    }

    public static bool TargetLacksReverseExit(Location target, string sourceLocationId) =>
        !target.Exits.Any(e => e.TargetLocationId == sourceLocationId);
}