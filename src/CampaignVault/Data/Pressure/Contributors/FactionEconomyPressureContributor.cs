using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class FactionEconomyPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 60;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.RelevantFactions == null)
        {
            return pressures;
        }

        List<Item>? partyItemsCache = null;
        foreach (var f in ctx.Scene.RelevantFactions)
        {
            if (f.EconomicDemand == null || !f.EconomicDemand.Any())
            {
                continue;
            }

            var highDemands = f.EconomicDemand.Where(kvp => kvp.Value >= 1.5f).Select(kvp => kvp.Key).ToList();
            if (!highDemands.Any())
            {
                continue;
            }

            partyItemsCache ??= await PressureHelpers.LoadPartyInventoryAsync(ctx.Session, ctx.Scene, ctx.CampaignName);
            foreach (var demand in highDemands)
            {
                if (partyItemsCache.Any(i => PressureHelpers.ItemMatchesEconomicDemand(i, demand)))
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, f.FactionId,
                        $"The local faction '{f.Name}' is desperate for '{demand}' due to recent events. Merchants will pay a premium, and thieves may attempt to steal them. Highlight this in your narration.",
                        $"Faction:EconomicDemand:{f.FactionId}:{demand}"));
                }
            }
        }

        return pressures;
    }
}