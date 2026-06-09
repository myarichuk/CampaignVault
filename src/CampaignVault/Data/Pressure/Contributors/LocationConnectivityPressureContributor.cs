using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class LocationConnectivityPressureContributor : IPressureContributor
{
    public const string MissingReverseLinkGroupingKey = "Location:MissingReverseLink";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 20;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || !ctx.Scene.IsLocationAnchored)
        {
            return pressures;
        }

        var loc = ctx.Scene.Location;
        if (!string.IsNullOrEmpty(loc.ParentLocationId))
        {
            try
            {
                var parentLoc = await ctx.Session.LoadAsync<Location>(loc.ParentLocationId, ct);
                if (parentLoc != null && !parentLoc.Exits.Any(e => e.TargetLocationId == loc.Id))
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, parentLoc.Id,
                        $"This location has a ParentLocationId but the parent has no matching exit back to it (one-way link / broken connectivity). " +
                        "Fix with location_update on the parent:\n" +
                        "[ { \"$type\": \"location_update\", \"locationId\": \"" + parentLoc.Id + "\", " +
                        "\"addExit\": { \"targetLocationId\": \"" + loc.Id + "\", \"description\": \"... (back to " + loc.Name + ")\" } } ]",
                        MissingReverseLinkGroupingKey));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pressure check error: {ex.Message}");
            }
        }

        return pressures;
    }
}