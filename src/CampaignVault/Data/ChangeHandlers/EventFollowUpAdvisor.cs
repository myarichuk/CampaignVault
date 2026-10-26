using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Reads a commit batch and emits one-line hints when a tracked narrative event (conversation, discovery,
/// betrayal) arrives without the follow-up changes that usually accompany it (activity, knowledge_update,
/// relationship/engagement). Advisory only: nothing here fails the batch.
///
/// Token budget: every hint used to quote the event's full <see cref="EventOccurred.Summary"/> (the caller's
/// own narrative, often a few hundred chars) and carried a sentence of rationale, two to four times per event,
/// on every turn that logged a conversation. The hints now name the event by its id or a short snippet and keep
/// the rationale to a clause; the "no 'x' commit found" phrasing is stable because clients and tests key on it.
/// </summary>
internal static class EventFollowUpAdvisor
{
    private const int SnippetChars = 48;

    public static IReadOnlyList<string> Apply(WorldChange[] changes)
    {
        var tracked = changes.OfType<EventOccurred>()
            .Where(e => e.Involved is { Count: > 0 } && e.Category is EventCategory.Conversation or EventCategory.Discovery or EventCategory.Betrayal)
            .ToList();

        if (tracked.Count == 0)
        {
            return [];
        }

        var activityCharacterIds = new HashSet<string>(
            changes.OfType<ActivityChange>().Select(a => a.CharacterId),
            StringComparer.OrdinalIgnoreCase);

        var knowledgeUpdates = changes.OfType<KnowledgeUpdate>().Where(k => k.CreateMemory).ToList();

        var knowledgeCharacterIds = new HashSet<string>(
            knowledgeUpdates.Select(k => k.CharacterId),
            StringComparer.OrdinalIgnoreCase);

        var sourcedKnowledgeCharacterIds = new HashSet<string>(
            knowledgeUpdates.Where(k => k.SourceEventIds is { Count: > 0 }).Select(k => k.CharacterId),
            StringComparer.OrdinalIgnoreCase);

        var relationshipCharacterIds = new HashSet<string>(
            changes.OfType<RelationshipChange>().SelectMany(r => new[] { r.CharacterId, r.TargetId })
                .Concat(changes.OfType<EngagementRelationChange>().SelectMany(r => new[] { r.CharacterId, r.TargetId })),
            StringComparer.OrdinalIgnoreCase);

        var notes = new List<string>();
        foreach (var ev in tracked)
        {
            var involved = ev.Involved!;
            var label = Ref(ev);
            var hasActivity = involved.Any(activityCharacterIds.Contains);
            var hasKnowledge = involved.Any(knowledgeCharacterIds.Contains);

            if (!hasActivity)
            {
                var why = ev.Category switch
                {
                    EventCategory.Betrayal => "posture or positioning usually changes (recoiling, drawing steel, fleeing)",
                    EventCategory.Discovery => "if it changed what someone is doing or where they are",
                    _ => "if body language or blocking changed",
                };
                notes.Add($"Hint: no 'activity' commit found for {label} — {why}, commit one so the scene reflects it.");
            }

            if (!hasKnowledge)
            {
                var why = ev.Category switch
                {
                    EventCategory.Betrayal => "highly salient: commit knowledge_update for the affected characters (Important/Core, Negative/Traumatic)",
                    EventCategory.Discovery => "a discovery is usually worth a memory (what was found, where)",
                    _ => "if anyone learned or revealed something worth remembering, commit knowledge_update so it persists",
                };
                notes.Add($"Hint: no 'knowledge_update' commit found for {label} — {why}.");
            }

            if (ev.Category == EventCategory.Betrayal && !involved.Any(relationshipCharacterIds.Contains))
            {
                notes.Add($"Hint: no 'relationship_change' or 'engagement_relation' found for {label} — a betrayal usually shifts how these characters view each other.");
            }

            // A memory was committed for a participant but doesn't trace back to ground truth. Soft reminder only: the
            // referenced id can't be validated here (it may be a prior event, or this event's own client-set eventId).
            if (hasKnowledge && !involved.Any(sourcedKnowledgeCharacterIds.Contains))
            {
                notes.Add($"Hint: the knowledge_update for {label} sets no 'sourceEventIds' — reference this event's 'eventId' (or a prior event's id) so the memory stays traceable.");
            }

            var untaggedMemory = knowledgeUpdates.Any(k =>
                k.CharacterId != null
                && involved.Contains(k.CharacterId, StringComparer.OrdinalIgnoreCase)
                && (k.RelatedEntityIds == null || k.RelatedEntityIds.Count == 0));
            if (untaggedMemory && involved.Any(id => !id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"Hint: the knowledge_update for {label} has no 'relatedEntityIds' — list the NPCs/items/places it concerns so the memory resurfaces when they appear.");
            }
        }

        return notes;
    }

    /// <summary>The event's client-set id when present, else a short quoted snippet of its summary.</summary>
    private static string Ref(EventOccurred ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.EventId))
        {
            return $"event '{ev.EventId.Trim()}'";
        }

        var summary = (ev.Summary ?? string.Empty).Trim();
        return summary.Length <= SnippetChars
            ? $"\"{summary}\""
            : $"\"{summary[..SnippetChars].TrimEnd()}…\"";
    }
}
