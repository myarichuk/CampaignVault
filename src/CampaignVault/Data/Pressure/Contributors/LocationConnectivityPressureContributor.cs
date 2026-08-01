using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class LocationConnectivityPressureContributor : IPressureContributor
{
    public const string MissingReverseLinkGroupingKey = "Location:MissingReverseLink";

    private readonly ILogger<LocationConnectivityPressureContributor>? _logger;

    public LocationConnectivityPressureContributor(ILogger<LocationConnectivityPressureContributor>? logger = null)
    {
        _logger = logger;
    }

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
                if (parentLoc != null && LocationConnectivitySuggestions.TargetLacksReverseExit(parentLoc, loc.Id))
                {
                    var suggested = LocationConnectivitySuggestions.BuildReverseExitCommitJson(
                        parentLoc.Id, loc.Id, loc.Name);
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, parentLoc.Id,
                        $"This location has a ParentLocationId but the parent has no matching exit back to it (one-way link / broken connectivity). " +
                        "Fix with location_update on the parent.",
                        MissingReverseLinkGroupingKey)
                    {
                        SuggestedCommitJson = suggested
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Location connectivity pressure check failed");
            }
        }

        foreach (var exit in loc.Exits.Where(e => !e.OneWay))
        {
            try
            {
                var targetLoc = await ctx.Session.LoadAsync<Location>(exit.TargetLocationId, ct);
                if (targetLoc == null)
                {
                    continue;
                }

                if (LocationConnectivitySuggestions.TargetLacksReverseExit(targetLoc, loc.Id))
                {
                    var suggested = LocationConnectivitySuggestions.BuildReverseExitCommitJson(
                        targetLoc.Id, loc.Id, loc.Name);
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, targetLoc.Id,
                        $"Exit from '{loc.Name}' to '{targetLoc.Name}' has no return path (accidental one-way link). " +
                        $"Add a reverse exit on '{targetLoc.Name}' or mark the forward exit oneWay: true if intentional.",
                        MissingReverseLinkGroupingKey)
                    {
                        SuggestedCommitJson = suggested
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Location connectivity pressure check failed");
            }
        }

        return pressures;
    }
}