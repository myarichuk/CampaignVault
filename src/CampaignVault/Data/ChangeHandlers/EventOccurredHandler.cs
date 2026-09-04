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
        var id = await ResolveEventIdAsync(ev.EventId, context, ct);

        // Resolve importance: explicit > Deliberate floor > category default
        var importance = ev.Importance ?? ResolveImportanceForCategory(ev.Category, ev.RecordingMode);

        // Recover a location ID mistakenly placed in 'involved' instead of 'locationId' — mirrors
        // ConversationInvolvedResolver's auto-inference for 'involved' itself, so this field has the
        // same safety net. See EventOccurredHandler's LocationId documentation for the failure mode.
        var locationId = ev.LocationId
            ?? ev.Involved?.FirstOrDefault(id2 => id2.StartsWith("locations/", StringComparison.OrdinalIgnoreCase));
        if (locationId != null && ev.LocationId == null)
        {
            context.RecordMessage(
                $"NOTE: 'locationId' was omitted but '{locationId}' was found in 'involved' — used it as the event's location. " +
                "Prefer setting 'locationId' explicitly next time.");
        }

        var e = new Event
        {
            Id = id,
            Summary = ev.Summary,
            Category = ev.Category,
            Involved = ev.Involved ?? [],
            DayLogged = currentTime.TotalDaysElapsed,
            EmotionalBeat = ev.EmotionalBeat,
            InitiatorId = ev.InitiatorId,
            RelatedEntityId = ev.RelatedEntityId,
            LocationId = locationId,
            RelatedLocationIds = ev.RelatedLocationIds,
            Importance = importance,
            Details = ev.Details
        };

        e.CampaignName = context.CampaignName;

        await context.LogEventAsync(e);
        // Don't echo ev.Summary back — the caller just supplied that exact text in this same
        // request; repeating it costs tokens for zero new information.
        context.RecordMessage($"Event logged (id: {e.Id}).");

        // Skip novelty scoring for engine/bookkeeping-generated categories (transient eviction departures,
        // timeskip/simulation logging, crowd interrupts) — these are auto-narrated, not LLM narrative
        // choices, so "is this novel" adds no DM value. Also avoids an extra query per event on hot
        // simulation paths (e.g. TransientEvictionRule emitting many Departure events per AdvanceWorld tick).
        if (ev.Category is not (EventCategory.Departure or EventCategory.Timeskip or EventCategory.Simulation
            or EventCategory.SceneInterrupt or EventCategory.Test))
        {
            var (similarity, noveltyHint) = await EventNoveltyAdvisor.ScoreAsync(context, e, ct);
            e.NoveltyScore = similarity;
            if (noveltyHint != null)
            {
                context.RecordMessage(noveltyHint);
            }
        }

        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// Default importance by category when the LLM omits an explicit value. Mirrors the same
    /// bookkeeping-category split used to skip novelty scoring above — kept as a single source of truth.
    /// </summary>
    private static MemoryImportance DefaultImportanceFor(EventCategory category) => category switch
    {
        EventCategory.Departure or EventCategory.Timeskip or EventCategory.Simulation
            or EventCategory.SceneInterrupt or EventCategory.Test => MemoryImportance.Trivial,
        _ => MemoryImportance.Important
    };

    /// <summary>
    /// Resolves importance accounting for Deliberate recording mode: when Deliberate, floors at Important
    /// unless the default was already Important or higher.
    /// </summary>
    private static MemoryImportance ResolveImportanceForCategory(EventCategory category, RecordingMode? recordingMode)
    {
        var defaultImportance = DefaultImportanceFor(category);

        // Deliberate recording floors at Important (e.g., intentional wilderness landmark discovery marked deliberately)
        if (recordingMode == RecordingMode.Deliberate && defaultImportance == MemoryImportance.Trivial)
        {
            return MemoryImportance.Important;
        }

        return defaultImportance;
    }

    /// <summary>
    /// Resolves the ID for a newly logged event. Honors a client-supplied EventId (normalized to the
    /// "events/" prefix) so other changes in the same commit batch can reference it via sourceEventIds.
    /// On a collision with an existing event, falls back to a generated ID rather than overwriting —
    /// unlike LocationCreate's upsert-on-collision, events are an append-only log and silent overwrite
    /// would destroy prior history.
    /// </summary>
    private static async Task<string> ResolveEventIdAsync(string? requestedId, ChangeContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            return "events/" + Guid.NewGuid();
        }

        var id = requestedId.StartsWith("events/", StringComparison.OrdinalIgnoreCase)
            ? requestedId
            : "events/" + requestedId;

        if (context.Session != null)
        {
            var existing = await context.Session.LoadAsync<Event>(id, ct);
            if (existing != null)
            {
                var fallbackId = "events/" + Guid.NewGuid();
                context.RecordMessage(
                    $"WARNING: eventId '{id}' already exists; generated a new ID instead: {fallbackId}.");
                return fallbackId;
            }
        }

        return id;
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
                    if (id.StartsWith("chars/"))
                    {
                        characterIds?.Add(id);
                        allInvolvedIds?.Add(id);
                    }
                    else if (id.StartsWith("locations/"))
                    {
                        locationIds?.Add(id);
                        allInvolvedIds?.Add(id);
                    }
                    else if (id.StartsWith("factions/"))
                    {
                        factionIds?.Add(id);
                        allInvolvedIds?.Add(id);
                    }
                    else if (id.StartsWith("quests/"))
                    {
                        questIds?.Add(id);
                        allInvolvedIds?.Add(id);
                    }
                    else if (id.StartsWith("items/"))
                    {
                        itemIds?.Add(id);
                        allInvolvedIds?.Add(id);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(eo.RelatedEntityId))
        {
            if (eo.RelatedEntityId.StartsWith("chars/"))
            {
                characterIds?.Add(eo.RelatedEntityId);
                allInvolvedIds?.Add(eo.RelatedEntityId);
            }
            else if (eo.RelatedEntityId.StartsWith("locations/"))
            {
                locationIds?.Add(eo.RelatedEntityId);
                allInvolvedIds?.Add(eo.RelatedEntityId);
            }
            else if (eo.RelatedEntityId.StartsWith("factions/"))
            {
                factionIds?.Add(eo.RelatedEntityId);
                allInvolvedIds?.Add(eo.RelatedEntityId);
            }
            else if (eo.RelatedEntityId.StartsWith("quests/"))
            {
                questIds?.Add(eo.RelatedEntityId);
                allInvolvedIds?.Add(eo.RelatedEntityId);
            }
            else if (eo.RelatedEntityId.StartsWith("items/"))
            {
                itemIds?.Add(eo.RelatedEntityId);
                allInvolvedIds?.Add(eo.RelatedEntityId);
            }
        }

        if (!string.IsNullOrEmpty(eo.LocationId))
        {
            locationIds?.Add(eo.LocationId);
            allInvolvedIds?.Add(eo.LocationId);
        }

        if (eo.RelatedLocationIds != null)
        {
            foreach (var id in eo.RelatedLocationIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    locationIds?.Add(id);
                    allInvolvedIds?.Add(id);
                }
            }
        }

        return true;
    }
}