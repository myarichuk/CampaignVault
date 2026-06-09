using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class FactionOpportunisticPressureContributor : IPressureContributor
{
    public static string GetOpportunisticGroupingKey(string factionId) => $"Faction:Opportunistic:{factionId}";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 55;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.RelevantFactions == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var f in ctx.Scene.RelevantFactions.Where(f => f.LocalStance == FactionStance.Opportunistic))
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, f.FactionId,
                $"An Opportunistic faction ('{f.Name}') is present. Evaluate the visual tags, current appearance, and item states of the characters. " +
                "If they appear wealthy, exhausted, or otherwise vulnerable, narrate an attempt to exploit, rob, or ambush them.",
                GetOpportunisticGroupingKey(f.FactionId)));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}