using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Nudges the LLM DM toward commonly-forgotten follow-up commits for narratively significant
/// event categories: Conversation, Discovery, and Betrayal. Purely rule-based (no model
/// inference) and advisory only — never blocks or fails the commit.
/// </summary>
internal static class EventFollowUpAdvisor
{
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

        var knowledgeCharacterIds = new HashSet<string>(
            changes.OfType<KnowledgeUpdate>().Where(k => k.CreateMemory).Select(k => k.CharacterId),
            StringComparer.OrdinalIgnoreCase);

        var sourcedKnowledgeCharacterIds = new HashSet<string>(
            changes.OfType<KnowledgeUpdate>().Where(k => k.CreateMemory && k.SourceEventIds is { Count: > 0 }).Select(k => k.CharacterId),
            StringComparer.OrdinalIgnoreCase);

        var relationshipCharacterIds = new HashSet<string>(
            changes.OfType<RelationshipChange>().SelectMany(r => new[] { r.CharacterId, r.TargetId }),
            StringComparer.OrdinalIgnoreCase);

        var engagementCharacterIds = new HashSet<string>(
            changes.OfType<EngagementRelationChange>().SelectMany(r => new[] { r.CharacterId, r.TargetId }),
            StringComparer.OrdinalIgnoreCase);

        var notes = new List<string>();
        foreach (var ev in tracked)
        {
            var involved = ev.Involved!;

            switch (ev.Category)
            {
                case EventCategory.Conversation:
                    if (!involved.Any(activityCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'activity' commit found for any participant in \"{ev.Summary}\" — if body language/blocking changed (pacing, sitting down heavily, leaning in), commit an activity update so get_scene reflects it.");
                    }

                    if (!involved.Any(knowledgeCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'knowledge_update' commit found for any participant in \"{ev.Summary}\" — if anyone learned or revealed something worth remembering, commit knowledge_update so it persists as an NPC memory (with decay/importance).");
                    }

                    break;

                case EventCategory.Discovery:
                    if (!involved.Any(activityCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'activity' commit found for any participant in \"{ev.Summary}\" — if the discovery changed what someone is doing or where they are (e.g. crouching to examine it, moving to investigate), commit an activity update so get_scene reflects it.");
                    }

                    if (!involved.Any(knowledgeCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'knowledge_update' commit found for any participant in \"{ev.Summary}\" — discoveries are usually worth a memory (what was found, where) so the character can act on it or share it later.");
                    }

                    break;

                case EventCategory.Betrayal:
                    if (!involved.Any(activityCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'activity' commit found for any participant in \"{ev.Summary}\" — a betrayal usually changes posture or positioning (recoiling, drawing a weapon, fleeing); commit an activity update if so.");
                    }

                    if (!involved.Any(knowledgeCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'knowledge_update' commit found for any participant in \"{ev.Summary}\" — betrayals are highly salient; commit knowledge_update for affected characters (likely Important/Core importance, Negative/Traumatic valence).");
                    }

                    if (!involved.Any(relationshipCharacterIds.Contains) && !involved.Any(engagementCharacterIds.Contains))
                    {
                        notes.Add(
                            $"Hint: no 'relationship_change' or 'engagement_relation' found for \"{ev.Summary}\" — a betrayal usually shifts how these characters view or interact with each other; consider committing one.");
                    }

                    break;
            }

            // Applies uniformly across all tracked categories: a knowledge_update was committed for a
            // participant, but doesn't trace back to ground truth. Soft reminder only — the referenced
            // ID can't be validated here (it may point at a prior commit's event, or at this event's
            // own client-supplied 'eventId', which this handler hasn't resolved yet).
            if (involved.Any(knowledgeCharacterIds.Contains) && !involved.Any(sourcedKnowledgeCharacterIds.Contains))
            {
                notes.Add(
                    $"Hint: the knowledge_update(s) tied to \"{ev.Summary}\" don't set 'sourceEventIds' — if this memory stems from this beat, reference it via a client-set 'eventId' on this event change (or a prior event's ID) so ground truth stays traceable.");
            }

            // Nudge about relatedEntityIds: if knowledge_update exists but entities involved are not tracked
            var knowledgeUpdatesForEvent = changes.OfType<KnowledgeUpdate>()
                .Where(k => k.CharacterId != null && involved.Contains(k.CharacterId, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (knowledgeUpdatesForEvent.Any(k => k.RelatedEntityIds == null || k.RelatedEntityIds.Count == 0))
            {
                var otherInvolved = involved.Where(id => !id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (otherInvolved.Count > 0)
                {
                    notes.Add(
                        $"Hint: the knowledge_update(s) for \"{ev.Summary}\" don't populate 'relatedEntityIds' — if other entities (NPCs, items, locations) are contextually related to this memory, list them so the engine can surface relevant memories when those entities appear later.");
                }
            }
        }

        return notes;
    }
}
