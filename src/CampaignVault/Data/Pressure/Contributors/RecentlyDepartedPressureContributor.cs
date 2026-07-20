using System.Text.Json.Nodes;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Surfaces a narrative prompt when the party returns to a location where transient NPCs have recently departed.
/// Emits a suggested commit to re-anchor and re-promote the departed NPC if desired.
/// </summary>
public sealed class RecentlyDepartedPressureContributor : IPressureContributor
{
    public const string RecentlyDepartedGroupingKey = "Location:RecentlyDeparted";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 25;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || !ctx.Scene.IsLocationAnchored)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var loc = ctx.Scene.Location;
        if (loc.RecentlyDeparted.Count == 0)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var names = string.Join(", ", loc.RecentlyDeparted.Select(d => d.Name));

        // Build suggested world_build calls to re-anchor departed NPCs.
        var suggests = string.Join("\n", loc.RecentlyDeparted.Select(departed =>
        {
            var body = new JsonObject
            {
                ["characters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = departed.CharacterId,
                        ["name"] = departed.Name,
                        ["keepAlive"] = true,
                        ["currentLocationId"] = loc.Id,
                        ["currentActivity"] = $"Returning to {loc.Name}"
                    }
                }
            };
            return body.ToJsonString();
        }));

        pressures.Add(new WorldPressureItem(
            PressureSeverity.NarrativePrompt,
            loc.Id,
            $"Recently departed NPCs at '{loc.Name}': {names}. If the party encounters them again and you wish to reintroduce them, call world_build to re-anchor them at this location:\n{suggests}",
            RecentlyDepartedGroupingKey));

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}
