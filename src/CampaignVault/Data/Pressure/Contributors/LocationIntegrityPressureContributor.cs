using CampaignVault.Data;
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
            var anyPartyMemberPresent = ctx.Scene.PresentNPCs.Any(c => c.IsPc || c.IsPartyCompanion);
            if (!anyPartyMemberPresent)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, loc.Id,
                    $"You narrated the party exploring '{loc.Id}', but the engine shows NO party members (isPc / isPartyCompanion) present here! " +
                    "Did you forget to commit their travel? Use the `commit` tool with a `travel` or `location_update` change immediately:\n" +
                    "[ { \"$type\": \"travel\", \"characterId\": \"...\", \"destinationLocationId\": \"" + loc.Id + "\", \"narrative\": \"They arrive at the location.\" } ]",
                    MissingTravelCommitGroupingKey));
            }
        }

        if (loc.Exits.Count == 0 && loc.Type != LocationType.Region)
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, loc.Id,
                $"This location has no Exits. The players are soft-locked. " +
                "Use `location_update` to add an exit back.",
                NoExitsGroupingKey)
            {
                SuggestedCommitJson = LocationConnectivitySuggestions.BuildNoExitsCommitJson(loc.Id)
            });
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}