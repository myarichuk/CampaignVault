using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class StuckTravelPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.Both;
    public int Order => 45;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene?.PresentNPCs != null)
        {
            foreach (var npc in ctx.Scene.PresentNPCs)
            {
                if (npc.CurrentActivity != null && npc.CurrentActivity.Contains("interrupted en route", StringComparison.OrdinalIgnoreCase))
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, npc.Id,
                        $"Character '{npc.Name}' is stuck: '{npc.CurrentActivity}'. Narrate the encounter resolution then commit e.g. [ {{\"$type\": \"activity\", \"characterId\": \"{npc.Id}\", \"newActivity\": \"...resolved...\", \"updateLocation\": false }}, {{\"$type\": \"travel\", \"characterId\": \"{npc.Id}\", \"destinationLocationId\": \"...\", \"encounterRiskModifier\": -20 }} ] to continue.",
                        "Travel:Interrupted"));
                }
            }

            return pressures;
        }

        var candidates = await PressureQueryHelper.QueryCharactersWithActivityAsync(ctx.Session, ctx.CampaignName, 20, ct);
        var stuck = candidates
            .Where(c => c.CurrentActivity != null
                        && (c.CurrentActivity.StartsWith("Travel interrupted en route", StringComparison.OrdinalIgnoreCase)
                            || c.CurrentActivity.StartsWith("interrupted en route", StringComparison.OrdinalIgnoreCase)))
            .Take(5)
            .ToList();

        foreach (var s in stuck)
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, s.Id,
                $"Character '{s.Name}' is stuck: '{s.CurrentActivity}'. Narrate the encounter resolution then commit e.g. [ {{\"$type\": \"activity\", \"characterId\": \"{s.Id}\", \"newActivity\": \"...resolved...\", \"updateLocation\": false }}, {{\"$type\": \"travel\", \"characterId\": \"{s.Id}\", \"destinationLocationId\": \"...\", \"encounterRiskModifier\": -20 }} ] to continue.",
                "Travel:Interrupted"));
        }

        return pressures;
    }
}