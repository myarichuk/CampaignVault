using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles EventOccurred. Uses context hooks for time and logging.
/// </summary>
public sealed class EventOccurredHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is EventOccurred;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var ev = (EventOccurred)change;

        if (ev.Category == EventCategory.Conversation && (ev.Involved == null || ev.Involved.Count == 0))
        {
            return ChangeHandlerResult.Failure("Events of category 'Conversation' MUST specify the 'involved' property containing the character IDs of the participants so they can recall it.");
        }

        var currentTime = await context.GetCurrentTimeAsync();
        var e = new Event
        {
            Id = "events/" + Guid.NewGuid(),
            Summary = ev.Summary,
            Category = ev.Category,
            Involved = ev.Involved ?? [],
            DayLogged = currentTime.TotalDaysElapsed,
            EmotionalBeat = ev.EmotionalBeat,
            RelatedEntityId = ev.RelatedEntityId
        };

        e.CampaignName = context.CampaignName;

        await context.LogEventAsync(e);
        context.RecordMessage($"Event logged: {ev.Summary}");

        return ChangeHandlerResult.Ok;
    }
}