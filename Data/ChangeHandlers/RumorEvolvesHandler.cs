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
            throw new InvalidOperationException("RumorEvolvesHandler requires a non-null session for Patch operations.");

        context.Session.Advanced.Patch<Rumor, RumorState>(rumor.RumorId, x => x.State, rumor.NewState);

        if (rumor.NewText != null)
            context.Session.Advanced.Patch<Rumor, string>(rumor.RumorId, x => x.CurrentText, rumor.NewText);

        var rtime = await context.GetCurrentTimeAsync();
        context.Session.Advanced.Patch<Rumor, int>(rumor.RumorId, x => x.LastStateChangeDay, rtime.TotalDaysElapsed);

        context.RecordMessage($"Rumor {rumor.RumorId} evolved to {rumor.NewState}");

        return ChangeHandlerResult.Ok;
    }
}