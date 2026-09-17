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

    public PressureScope Scope => PressureScope.Both;
    public int Order => 25;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        string locId;
        string locName;
        List<DepartedNpcRecord> recentlyDeparted;

        if (ctx.Scene is { IsLocationAnchored: true })
        {
            locId = ctx.Scene.Location.Id;
            locName = ctx.Scene.Location.Name;
            recentlyDeparted = ctx.Scene.Location.RecentlyDeparted;
        }
        else if (!string.IsNullOrEmpty(ctx.RequestedLocationId) && ctx.PartyPresent)
        {
            // Reachable from take_turn/get_world_state (World scope), not just get_scene — mirrors
            // AmbientCrowdPressureContributor's sparse-crowd path: no full SceneView needed, just the
            // Location doc, which already carries RecentlyDeparted.
            var loaded = await ctx.Session.LoadAsync<Location>(ctx.RequestedLocationId, ct);
            if (loaded == null)
            {
                return pressures;
            }

            locId = loaded.Id;
            locName = loaded.Name;
            recentlyDeparted = loaded.RecentlyDeparted;
        }
        else
        {
            return pressures;
        }

        if (recentlyDeparted.Count == 0)
        {
            return pressures;
        }

        var names = string.Join(", ", recentlyDeparted.Select(d => d.Name));

        // Build suggested world_build calls to re-anchor departed NPCs.
        var suggests = string.Join("\n", recentlyDeparted.Select(departed =>
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
                        ["currentLocationId"] = locId,
                        ["currentActivity"] = $"Returning to {locName}"
                    }
                }
            };
            return body.ToJsonString();
        }));

        pressures.Add(new WorldPressureItem(
            PressureSeverity.NarrativePrompt,
            locId,
            $"Recently departed NPCs at '{locName}': {names}. If the party encounters them again and you wish to reintroduce them, call world_build to re-anchor them at this location:\n{suggests}",
            RecentlyDepartedGroupingKey));

        return pressures;
    }
}
