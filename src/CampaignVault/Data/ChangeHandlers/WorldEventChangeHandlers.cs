using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class WorldEventStatusChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is WorldEventStatusChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var wesc = (WorldEventStatusChange)change;

        if (string.IsNullOrWhiteSpace(wesc.WorldEventId))
            return ChangeHandlerResult.Failure("WorldEventId is required.");

        var evt = await context.Session.LoadAsync<WorldEvent>(wesc.WorldEventId, ct);
        if (evt == null)
            return ChangeHandlerResult.Failure($"WorldEvent '{wesc.WorldEventId}' not found.");

        if (wesc.NewStatus.HasValue)
            evt.Status = wesc.NewStatus.Value;

        if (wesc.LastTriggeredDay.HasValue)
            evt.LastTriggeredDay = wesc.LastTriggeredDay.Value;

        if (!string.IsNullOrWhiteSpace(wesc.NarrativeNote))
            evt.DmNotes = string.IsNullOrWhiteSpace(evt.DmNotes)
                ? wesc.NarrativeNote
                : evt.DmNotes + "\n" + wesc.NarrativeNote;

        // Only update LastUpdatedDay for non-engine-authored changes
        if (!wesc.IsEngineAuthored)
        {
            var time = await context.GetCurrentTimeAsync();
            evt.LastUpdatedDay = (int)time.TotalDaysElapsed;
        }

        context.RecordMessage($"Updated world event '{evt.Title}': status={evt.Status}.");
        return ChangeHandlerResult.Ok;
    }

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not WorldEventStatusChange wesc) return false;
        return true;
    }
}
