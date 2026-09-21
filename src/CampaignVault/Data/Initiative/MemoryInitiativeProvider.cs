using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class MemoryInitiativeProvider : INpcInitiativeSignalProvider
{
    // Calibrate against real data before merging — see ItemDetailSemanticMatchThreshold (0.86) in
    // ItemChangeHandlers.cs and EventNoveltyAdvisor's novelty/echo cutoffs as reference points from
    // the same embedding model.
    private const double SemanticMatchThreshold = 0.55;

    private readonly record struct MemoryMatchResult(bool Matched, string? MatchReason);

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
            var match = MemoryMatchesScene(
                memory, presentIds, presentNames, locationName, locationId,
                ctx.TriggerVector, ctx.RecentEvents, ctx.NpcRecentEvents);
            if (!match.Matched)
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

            var framing = BuildFraming(memory, ctx.Location?.Name, match.MatchReason);
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

    private static MemoryMatchResult MemoryMatchesScene(
        MemoryNode memory,
        HashSet<string> presentIds,
        IReadOnlyList<string> presentNames,
        string? locationName,
        string? locationId,
        float[]? triggerVector,
        IReadOnlyList<Event> recentEvents,
        IReadOnlyList<Event> npcRecentEvents)
    {
        if (memory.RelatedEntityIds.Any(id => presentIds.Contains(id)))
        {
            return new MemoryMatchResult(true, null);
        }

        // Details is typed non-nullable (`= null!`) but legacy/malformed knowledge_update commits
        // can still persist it as null — guard the read side, not just the write side.
        var details = memory.Details ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(locationId)
            && (memory.Topic.Contains(locationId, StringComparison.OrdinalIgnoreCase)
                || details.Contains(locationId, StringComparison.OrdinalIgnoreCase)))
        {
            return new MemoryMatchResult(true, null);
        }

        if (!string.IsNullOrWhiteSpace(locationName)
            && (memory.Topic.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                || details.Contains(locationName, StringComparison.OrdinalIgnoreCase)))
        {
            return new MemoryMatchResult(true, null);
        }

        // TriggerCondition is an LLM-authored freeform predicate (e.g. a name, place, or topic)
        // checked against who/where is currently present in the scene.
        if (!string.IsNullOrWhiteSpace(memory.TriggerCondition))
        {
            if (presentNames.Any(name => memory.TriggerCondition.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(memory.TriggerCondition, StringComparison.OrdinalIgnoreCase)))
            {
                return new MemoryMatchResult(true, null);
            }

            if (!string.IsNullOrWhiteSpace(locationName)
                && (memory.TriggerCondition.Contains(locationName, StringComparison.OrdinalIgnoreCase)
                    || locationName.Contains(memory.TriggerCondition, StringComparison.OrdinalIgnoreCase)))
            {
                return new MemoryMatchResult(true, null);
            }

            if (!string.IsNullOrWhiteSpace(locationId)
                && memory.TriggerCondition.Contains(locationId, StringComparison.OrdinalIgnoreCase))
            {
                return new MemoryMatchResult(true, null);
            }
        }

        // No cheap/precise match — fall through to semantic comparison against this turn's
        // just-committed text and recent events, only when both sides have a vector.
        if (memory.SemanticVector is { Length: > 0 } memVec)
        {
            if (triggerVector is { Length: > 0 }
                && SemanticEnrichmentHelper.CosineSimilarity(memVec, triggerVector) >= SemanticMatchThreshold)
            {
                return new MemoryMatchResult(true, $"reminded of \"{memory.Topic}\"");
            }

            var bestEvent = recentEvents.Concat(npcRecentEvents)
                .Where(e => e.SemanticVector is { Length: > 0 })
                .Select(e => (Event: e, Sim: SemanticEnrichmentHelper.CosineSimilarity(memVec, e.SemanticVector!)))
                .OrderByDescending(x => x.Sim)
                .FirstOrDefault();
            if (bestEvent.Event != null && bestEvent.Sim >= SemanticMatchThreshold)
            {
                return new MemoryMatchResult(true, $"reminded of \"{memory.Topic}\" by {bestEvent.Event.Id}");
            }
        }

        return new MemoryMatchResult(false, null);
    }

    // Note: this method intentionally re-derives the best-match event (not just a similarity score)
    // rather than calling SemanticTriggerMatcher.BestSimilarity, since the GM-facing framing needs to
    // name *which* event fired the match, not just whether one did. DefaultRelevantMemorySelector's
    // ranking term only needs the score, so it uses the shared helper directly.

    private static string BuildFraming(MemoryNode memory, string? locationName, string? semanticMatchReason)
    {
        string baseFraming;
        if (memory.Valence == EmotionalValence.Traumatic)
        {
            baseFraming = locationName != null
                ? $"Painful memory tied to {locationName} — may tense, withdraw, or react sharply if it comes up."
                : "Painful memory tied to this scene — may tense, withdraw, or react sharply if it comes up.";
        }
        else if (memory.Valence == EmotionalValence.Negative)
        {
            baseFraming = $"Unsettling memory about \"{memory.Topic}\" — may become guarded if the subject arises.";
        }
        else
        {
            baseFraming = $"Salient memory about \"{memory.Topic}\" — may color how they engage with the scene.";
        }

        // Only the semantic-match path needs an explicit pointer back to the memory — entity/
        // location/TriggerCondition wins already read naturally without one.
        if (semanticMatchReason == null)
        {
            return baseFraming;
        }

        var sourceEventSuffix = memory.SourceEventIds.Count > 0
            ? $", source event {memory.SourceEventIds[0]}"
            : string.Empty;
        return $"{baseFraming} (memory topic \"{memory.Topic}\"{sourceEventSuffix})";
    }
}