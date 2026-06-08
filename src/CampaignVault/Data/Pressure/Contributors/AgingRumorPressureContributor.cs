using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class AgingRumorPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 10;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var threshold = ctx.Config.RumorAgingPressureDays;

        if (ctx.ActiveRumors == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var r in ctx.ActiveRumors.Where(r => ctx.Time.TotalDaysElapsed - r.LastStateChangeDay > threshold))
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, r.Id,
                $"Rumor '{r.Subject}' has been spreading for {ctx.Time.TotalDaysElapsed - r.LastStateChangeDay} days without resolution. " +
                "Consider evolving or resolving via commit: [ { \"$type\": \"rumor\", \"rumorId\": \"...\", \"newState\": \"Fading|Resolved\", \"newText\": \"...\" } ]",
                "Rumor:Aging"));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}