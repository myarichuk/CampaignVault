using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class UnresolvedEventPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 15;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var agingEvents = await PressureQueryHelper.QueryUnresolvedEventsAsync(ctx.Session, ctx.CampaignName, 5, ct);

        foreach (var e in agingEvents)
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, e.Id,
                $"Unresolved thread: '{e.Summary}' ({ctx.Time.TotalDaysElapsed - e.DayLogged} days old). " +
                "Resolve or advance via commit e.g. [ { \"$type\": \"event\", \"category\": \"Resolution\", \"summary\": \"...resolved...\", \"involved\": [\"" + (e.Involved?.FirstOrDefault() ?? "ids...") + "\"] } ] or convert to rumor.",
                "Event:Unresolved"));
        }

        return pressures;
    }
}