using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class MemoryInitiativeProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var npc = ctx.Npc;
        var psych = npc.Psychology ?? new PsychologyProfile();
        if (psych.Memories.Count == 0)
        {
            return [];
        }

        var presentIds = new HashSet<string>(
            ctx.PresentEntities.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);
        var presentNames = ctx.PresentEntities.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var locationName = ctx.Location?.Name;
        var locationId = ctx.Location?.Id;

        var candidates = new List<InitiativeCandidate>();
        foreach (var memory in psych.Memories.Values)
        {
            memory.ApplyMigrationDefaultsIfNeeded();
            if (!MemoryMatchesScene(memory, presentIds, presentNames, locationName, locationId))
            {
                continue;
            }

            if (memory.Salience < 0.4 && memory.Urgency < MemoryUrgency.High)
            {
                continue;
            }

            var urgency = memory.Urgency;
            if (memory.Valence == EmotionalValence.Traumatic && urgency < MemoryUrgency.High)
            {
                urgency = MemoryUrgency.High;
            }

            var weight = memory.Salience * 80;
            if (memory.Valence == EmotionalValence.Traumatic)
            {
                weight += 20;
            }

            var framing = BuildFraming(memory, ctx.Location?.Name);
            candidates.Add(new InitiativeCandidate(
                $"memory:{npc.Id}:{memory.Topic}",
                npc.Id,
                InitiativeDriver.Memory,
                urgency,
                framing,
                weight));
        }

        return candidates;
    }

    private static bool MemoryMatchesScene(
        MemoryNode memory,
        HashSet<string> presentIds,
        IReadOnlyList<string> presentNames,
        string? locationName,
        string? locationId)
    {
        if (memory.RelatedEntityIds.Any(id => presentIds.Contains(id)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(locationId)
            && (memory.Topic.Contains(locationId, StringComparison.OrdinalIgnoreCase)
                || memory.Details.Contains(locationId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(locationName)
            && (memory.Topic.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                || memory.Details.Contains(locationName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // TriggerCondition is an LLM-authored freeform predicate (e.g. a name, place, or topic)
        // checked against who/where is currently present in the scene.
        if (!string.IsNullOrWhiteSpace(memory.TriggerCondition))
        {
            if (presentNames.Any(name => memory.TriggerCondition.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(memory.TriggerCondition, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(locationName)
                && (memory.TriggerCondition.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                    || locationName.Contains(memory.TriggerCondition, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(locationId)
                && memory.TriggerCondition.Contains(locationId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFraming(MemoryNode memory, string? locationName)
    {
        if (memory.Valence == EmotionalValence.Traumatic)
        {
            return locationName != null
                ? $"Painful memory tied to {locationName} — may tense, withdraw, or react sharply if it comes up."
                : "Painful memory tied to this scene — may tense, withdraw, or react sharply if it comes up.";
        }

        if (memory.Valence == EmotionalValence.Negative)
        {
            return $"Unsettling memory about \"{memory.Topic}\" — may become guarded if the subject arises.";
        }

        return $"Salient memory about \"{memory.Topic}\" — may color how they engage with the scene.";
    }
}