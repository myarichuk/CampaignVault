using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class LocationIntegrityPressureContributor : IPressureContributor
{
    public const string MissingTravelCommitGroupingKey = "Location:MissingTravelCommit";
    public const string NoExitsGroupingKey = "Location:NoExits";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 15;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || !ctx.Scene.IsLocationAnchored)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var loc = ctx.Scene.Location;

        if (ctx.PartyPresent)
        {
            var anyPartyMemberPresent = ctx.Scene.PresentNPCs.Any(c => c.KeepAlive);
            if (!anyPartyMemberPresent)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, loc.Id,
                    $"You narrated the party exploring '{loc.Id}', but the engine shows NO main characters (KeepAlive) present here! " +
                    "Did you forget to commit their travel? Use the `commit` tool with a `travel` or `location_update` change immediately:\n" +
                    "[ { \"$type\": \"travel\", \"characterId\": \"...\", \"destinationLocationId\": \"" + loc.Id + "\", \"narrative\": \"They arrive at the location.\" } ]",
                    MissingTravelCommitGroupingKey));
            }
        }

        if (loc.Exits.Count == 0 && loc.Type != LocationType.Region)
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, loc.Id,
                $"This location has no Exits. The players are soft-locked. " +
                "Use `location_update` to add an exit back:\n" +
                "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", " +
                "\"addExit\": { \"targetLocationId\": \"locations/previous_area\", \"description\": \"...\" } } ]",
                NoExitsGroupingKey));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}