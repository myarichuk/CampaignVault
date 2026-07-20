using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class LocationHallucinationPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Location:Hallucinated";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 10;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || ctx.Scene.IsLocationAnchored || string.IsNullOrEmpty(ctx.RequestedLocationId))
        {
            return pressures;
        }

        var locationId = ctx.RequestedLocationId;
        var suggestions = await PressureHelpers.SuggestLocationsAsync(ctx.Session, locationId, ctx.CampaignName);
        var upsertBody = "{\n  \"locations\": [ {\n    \"id\": \"" + locationId + "\",\n    " +
            "\"name\": \"...\",\n    \"description\": \"...\",\n    \"connectedFromLocationId\": \"...\",\n    " +
            "\"connectionDescription\": \"...\"\n  } ]\n}";

        if (suggestions.Any())
        {
            var names = string.Join(", ", suggestions.Select(s => $"'{s.Id}' ({s.Name})"));
            pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, locationId,
                $"Location '{locationId}' not found. Did you mean one of these: {names}? " +
                "If so, use the correct ID. If it is truly new, call `world_build`:\n" + upsertBody,
                GroupingKey));
        }
        else
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, locationId,
                $"You requested '{locationId}' but it does not exist in the database! " +
                "You are hallucinating. Call the `world_build` tool immediately:\n" + upsertBody,
                GroupingKey));
        }

        return pressures;
    }
}