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
        ChangeContext context,
        CancellationToken ct = default)
    {
        var rumor = (RumorEvolves)change;

        if (context.Session is null)
        {
            throw new InvalidOperationException("RumorEvolvesHandler requires a non-null session for Patch operations.");
        }

        var existing = await context.Session.LoadAsync<Rumor>(rumor.RumorId, ct);
        if (existing is null)
        {
            return ChangeHandlerResult.Failure($"Rumor '{rumor.RumorId}' not found.");
        }

        context.Session.Advanced.Patch<Rumor, RumorState>(rumor.RumorId, x => x.State, rumor.NewState);

        if (rumor.NewText != null)
        {
            context.Session.Advanced.Patch<Rumor, string>(rumor.RumorId, x => x.CurrentText, rumor.NewText);
        }

        var rtime = await context.GetCurrentTimeAsync();
        context.Session.Advanced.Patch<Rumor, int>(rumor.RumorId, x => x.LastStateChangeDay, rtime.TotalDaysElapsed);

        context.RecordMessage($"Rumor {rumor.RumorId} evolved to {rumor.NewState}");

        return ChangeHandlerResult.Ok;
    }
}

public sealed class RumorCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RumorCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var rc = (RumorCreate)change;
        if (context.Session is null)
        {
            throw new InvalidOperationException("RumorCreateHandler requires a non-null session.");
        }

        var existing = await context.Session.LoadAsync<Rumor>(rc.RumorId, ct);
        if (existing is not null)
        {
            return ChangeHandlerResult.Failure($"Rumor '{rc.RumorId}' already exists. Use rumor_evolves to update it.");
        }

        var time = await context.GetCurrentTimeAsync();
        var rumor = new Rumor
        {
            Id = rc.RumorId,
            Subject = rc.Subject,
            CurrentText = rc.Text,
            State = RumorState.Nascent,
            DayCreated = time.TotalDaysElapsed,
            LastStateChangeDay = time.TotalDaysElapsed,
            CampaignName = context.CampaignName
        };

        if (rc.RelatedLocationIds != null && rc.RelatedLocationIds.Any())
        {
            rumor.RegionLocationId = rc.RelatedLocationIds.First();
        }
        else
        {
            rumor.RegionLocationId = "global";
        }

        await context.Session.StoreAsync(rumor, ct);
        context.RecordMessage($"Created rumor '{rc.Subject}'.");
        return ChangeHandlerResult.Ok;
    }
}