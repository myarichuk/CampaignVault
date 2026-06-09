using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class NeverVisitedTransientPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Location:NeverVisitedTransients";

    public PressureScope Scope => PressureScope.World;
    public int Order => 35;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var transients = await PressureQueryHelper.QueryTransientCharactersAsync(ctx.Session, ctx.CampaignName, 50, ct);

        var transientLocIds = transients.Select(c => c.CurrentLocationId).Where(id => !string.IsNullOrEmpty(id)).Distinct();
        foreach (var locId in transientLocIds)
        {
            var l = await ctx.Session.LoadAsync<Location>(locId, ct);
            if (l != null && l.LastVisitedDay == null)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, l.Id,
                    $"Location '{l.Name}' has never been visited but has transient NPCs. " +
                    "Consider visiting this location or setting keepAlive: true on important NPCs so they are not silently evicted.",
                    GroupingKey));
            }
        }

        return pressures;
    }
}