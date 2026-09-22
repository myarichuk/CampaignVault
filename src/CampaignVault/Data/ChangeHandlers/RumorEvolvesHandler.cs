using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles RumorEvolves using raw Patch (rumors are not pre-loaded in the current design).
/// </summary>
public sealed class RumorEvolvesHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RumorEvolves;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var rumor = (RumorEvolves)change;

        var existing = await ctx.Session.LoadAsync<Rumor>(rumor.RumorId, ct);
        if (existing is null)
        {
            return ChangeHandlerResult.Failure($"Rumor '{rumor.RumorId}' not found.");
        }

        ctx.Session.Advanced.Patch<Rumor, RumorState>(rumor.RumorId, x => x.State, rumor.NewState);

        if (rumor.NewText != null)
        {
            ctx.Session.Advanced.Patch<Rumor, string>(rumor.RumorId, x => x.CurrentText, rumor.NewText);
        }

        var rtime = await ctx.GetCurrentTimeAsync();
        ctx.Session.Advanced.Patch<Rumor, int>(rumor.RumorId, x => x.LastStateChangeDay, rtime.TotalDaysElapsed);

        return ChangeHandlerResult.Ok;
    }
}

public sealed class RumorCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RumorCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var rc = (RumorCreate)change;

        var existing = await ctx.Session.LoadAsync<Rumor>(rc.RumorId, ct);
        if (existing is not null)
        {
            return ChangeHandlerResult.Failure($"Rumor '{rc.RumorId}' already exists. Use rumor_evolves to update it.");
        }

        var time = await ctx.GetCurrentTimeAsync();
        var rumor = new Rumor
        {
            Id = rc.RumorId,
            Subject = rc.Subject,
            CurrentText = rc.Text,
            State = RumorState.Nascent,
            DayCreated = time.TotalDaysElapsed,
            LastStateChangeDay = time.TotalDaysElapsed,
            CampaignName = ctx.CampaignName
        };

        if (rc.RelatedLocationIds != null && rc.RelatedLocationIds.Any())
        {
            rumor.RegionLocationId = rc.RelatedLocationIds.First();
        }
        else
        {
            rumor.RegionLocationId = "global";
        }

        await ctx.Session.StoreAsync(rumor, ct);
        return ChangeHandlerResult.Ok;
    }
}