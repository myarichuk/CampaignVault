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
            return ChangeHandlerResult.Failure(
                "Events of category 'Conversation' MUST include 'involved': an array of character IDs for everyone who participated (e.g. [\"chars/valen\", \"chars/lirael-goldvein\"]). " +
                "Without this, get_npc_context cannot recall the conversation. " +
                "Add 'involved' explicitly, or include engagement_relation + activity for the same characters in the same commit batch so the engine can auto-infer. " +
                "Do NOT use 'participants' — the field name is 'involved'.");
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

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not EventOccurred eo) return false;

        if (eo.Involved != null)
        {
            foreach (var id in eo.Involved)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    if (id.StartsWith("chars/")) characterIds?.Add(id);
                    else if (id.StartsWith("locations/")) locationIds?.Add(id);
                    else if (id.StartsWith("factions/")) factionIds?.Add(id);
                    else if (id.StartsWith("quests/")) questIds?.Add(id);
                    else if (id.StartsWith("items/")) itemIds?.Add(id);
                    allInvolvedIds?.Add(id);
                }
            }
        }

        if (!string.IsNullOrEmpty(eo.RelatedEntityId))
        {
            if (eo.RelatedEntityId.StartsWith("chars/")) characterIds?.Add(eo.RelatedEntityId);
            else if (eo.RelatedEntityId.StartsWith("locations/")) locationIds?.Add(eo.RelatedEntityId);
            else if (eo.RelatedEntityId.StartsWith("factions/")) factionIds?.Add(eo.RelatedEntityId);
            else if (eo.RelatedEntityId.StartsWith("quests/")) questIds?.Add(eo.RelatedEntityId);
            else if (eo.RelatedEntityId.StartsWith("items/")) itemIds?.Add(eo.RelatedEntityId);
            allInvolvedIds?.Add(eo.RelatedEntityId);
        }

        return true;
    }
}