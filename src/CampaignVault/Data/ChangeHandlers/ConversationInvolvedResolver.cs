using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Ensures Conversation events carry participant IDs in <see cref="EventOccurred.Involved"/> so
/// <c>get_npc_context</c> and initiative heuristics can recall who spoke with whom.
/// </summary>
internal static class ConversationInvolvedResolver
{
    public static IReadOnlyList<string> Apply(WorldChange[] changes)
    {
        if (changes.Length == 0)
        {
            return [];
        }

        var batchParticipantIds = CollectBatchParticipantIds(changes);
        if (batchParticipantIds.Count == 0)
        {
            return [];
        }

        var notes = new List<string>();
        foreach (var change in changes.OfType<EventOccurred>())
        {
            if (change.Category != EventCategory.Conversation)
            {
                continue;
            }

            if (change.Involved is { Count: > 0 })
            {
                continue;
            }

            change.Involved = batchParticipantIds.ToList();
            notes.Add(
                $"Auto-inferred involved [{string.Join(", ", change.Involved)}] for Conversation event from other changes in the same commit batch. Prefer setting 'involved' explicitly on every Conversation event.");
        }

        return notes;
    }

    internal static HashSet<string> CollectBatchParticipantIds(IEnumerable<WorldChange> changes)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            switch (change)
            {
                case EngagementRelationChange er:
                    AddIfPresent(ids, er.CharacterId);
                    AddIfPresent(ids, er.TargetId);
                    break;
                case ActivityChange ac:
                    AddIfPresent(ids, ac.CharacterId);
                    break;
                case RelationshipChange rc:
                    AddIfPresent(ids, rc.CharacterId);
                    AddIfPresent(ids, rc.TargetId);
                    break;
                case KnowledgeUpdate ku:
                    AddIfPresent(ids, ku.CharacterId);
                    break;
                case RulesetAction ra:
                    AddIfPresent(ids, ra.CharacterId);
                    if (ra.TargetIds != null)
                    {
                        foreach (var targetId in ra.TargetIds)
                        {
                            AddIfPresent(ids, targetId);
                        }
                    }

                    break;
                case MoodChange mc:
                    AddIfPresent(ids, mc.CharacterId);
                    break;
                case HpChange hp:
                    AddIfPresent(ids, hp.CharacterId);
                    break;
            }
        }

        return ids;
    }

    private static void AddIfPresent(HashSet<string> ids, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            ids.Add(id);
        }
    }
}