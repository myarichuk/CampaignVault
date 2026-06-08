using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class FactionTerritoryPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 50;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.RelevantFactions == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var f in ctx.Scene.RelevantFactions)
        {
            if (!f.PlayerReputation.HasValue)
            {
                continue;
            }

            if (f.PlayerReputation.Value <= -50)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, f.FactionId,
                    $"The party is in territory influenced by '{f.Name}', a faction they have very low reputation with ({f.PlayerReputation.Value}). " +
                    "They should face immediate suspicion, hostility, or be denied services. Consider an ambush or confrontation.",
                    $"Faction:HostileTerritory:{f.FactionId}"));
            }
            else if (f.PlayerReputation.Value >= 50)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, f.FactionId,
                    $"The party is in territory influenced by '{f.Name}', a faction they are highly regarded by ({f.PlayerReputation.Value}). " +
                    "They should be welcomed, offered better prices, or given assistance.",
                    $"Faction:AlliedTerritory:{f.FactionId}"));
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}