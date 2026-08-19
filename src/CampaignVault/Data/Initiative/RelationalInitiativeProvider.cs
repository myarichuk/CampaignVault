using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class RelationalInitiativeProvider : INpcInitiativeSignalProvider
{
    public IReadOnlyList<InitiativeCandidate> GetCandidates(NpcInitiativeContext ctx)
    {
        var npc = ctx.Npc;
        var psych = npc.Psychology ?? new PsychologyProfile();
        var social = npc.Social ?? new SocialProfile();
        var presentIds = new HashSet<string>(
            ctx.PresentEntities.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);
        var presentById = ctx.PresentEntities.ToDictionary(e => e.Id, e => e, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<InitiativeCandidate>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in ctx.NpcRecentEvents)
        {
            if (GratitudeHeuristicHelper.IsStructuredGratitudeBeat(ev.EmotionalBeat))
            {
                var related = ev.RelatedEntityId ?? ev.Involved.FirstOrDefault(id => !id.Equals(npc.Id, StringComparison.OrdinalIgnoreCase)) ?? "unknown";
                AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                    $"gratitude:{npc.Id}:{related}",
                    npc.Id,
                    InitiativeDriver.Relational,
                    MemoryUrgency.Normal,
                    "Recently received kindness — may want to reciprocate with hospitality, a favor, or a sincere thank-you.",
                    75));
            }
            else if (GratitudeHeuristicHelper.SummaryMatchesHeuristic(
                         ev.Summary,
                         ctx.Config.GratitudeHeuristicTokens is { Count: > 0 }
                             ? ctx.Config.GratitudeHeuristicTokens
                             : GratitudeHeuristicHelper.DefaultTokens))
            {
                var related = ev.Involved.FirstOrDefault(id => !id.Equals(npc.Id, StringComparison.OrdinalIgnoreCase)) ?? "recent";
                AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                    $"gratitude:{npc.Id}:{related}",
                    npc.Id,
                    InitiativeDriver.Relational,
                    MemoryUrgency.Normal,
                    "Recently received kindness — may want to reciprocate with hospitality, a favor, or a sincere thank-you.",
                    60));
            }
            else if (string.Equals(ev.EmotionalBeat, "affection", StringComparison.OrdinalIgnoreCase))
            {
                var target = ev.RelatedEntityId ?? ev.Involved.FirstOrDefault(id => !id.Equals(npc.Id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(target))
                {
                    AddCandidate(candidates, seenKeys, BuildAffectionCandidate(
                        npc, target, presentById, $"affection:{npc.Id}:{target}", 85,
                        "Strong bond — may seek meaningful connection (conversation, shared activity)."));
                }
            }
            else if (string.Equals(ev.EmotionalBeat, "betrayal", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(ev.EmotionalBeat, "resentment", StringComparison.OrdinalIgnoreCase))
            {
                var target = ev.RelatedEntityId ?? ev.Involved.FirstOrDefault(id => !id.Equals(npc.Id, StringComparison.OrdinalIgnoreCase)) ?? "unknown";
                AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                    $"resentment:{npc.Id}:{target}",
                    npc.Id,
                    InitiativeDriver.Relational,
                    MemoryUrgency.High,
                    "Recent betrayal or slight — may be guarded, cold, or confrontational if pressed.",
                    70));
            }
        }

        foreach (var item in ctx.NpcHeldItems.Where(GratitudeHeuristicHelper.ItemSuggestsGift))
        {
            AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                $"gratitude:{npc.Id}:{item.Id}",
                npc.Id,
                InitiativeDriver.Relational,
                MemoryUrgency.Normal,
                "Recently came into possession of something that may be a gift — may feel grateful or obliged.",
                55));
        }

        foreach (var memory in psych.Memories.Values)
        {
            memory.ApplyMigrationDefaultsIfNeeded();
            if (memory.Valence != EmotionalValence.Positive || memory.Source != MemorySource.Experienced)
            {
                continue;
            }

            if (ctx.CurrentDay - memory.DayAcquired > 2)
            {
                continue;
            }

            var related = memory.RelatedEntityIds.FirstOrDefault(id => presentIds.Contains(id));
            if (related == null)
            {
                continue;
            }

            AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                $"gratitude:{npc.Id}:{related}",
                npc.Id,
                InitiativeDriver.Relational,
                memory.Urgency >= MemoryUrgency.High ? memory.Urgency : MemoryUrgency.Normal,
                "Recently received kindness — may want to reciprocate with hospitality, a favor, or a sincere thank-you.",
                65 + memory.Salience * 20));
        }

        foreach (var (targetId, value) in social.Relationships)
        {
            if (!presentIds.Contains(targetId))
            {
                continue;
            }

            var persistentKey = RelationalInitiativeKeys.TryGetPersistentKey(npc.Id, targetId, value);
            if (persistentKey == null)
            {
                continue;
            }

            if (value >= 80)
            {
                AddCandidate(candidates, seenKeys, BuildAffectionCandidate(
                    npc, targetId, presentById, persistentKey, 80,
                    "Strong bond — may seek meaningful connection (conversation, shared activity)."));
            }
            else if (value >= 60)
            {
                AddCandidate(candidates, seenKeys, BuildAffectionCandidate(
                    npc, targetId, presentById, persistentKey, 55,
                    "Feels warmly — may check in, share news, or offer comfort."));
            }
            else if (value <= -60)
            {
                var name = presentById.TryGetValue(targetId, out var target) ? target.Name : "someone present";
                AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                    persistentKey,
                    npc.Id,
                    InitiativeDriver.Relational,
                    MemoryUrgency.High,
                    $"Strong resentment toward {name} — may be guarded, sharp, or avoidant.",
                    65));
            }
            else
            {
                var name = presentById.TryGetValue(targetId, out var target) ? target.Name : "someone present";
                AddCandidate(candidates, seenKeys, new InitiativeCandidate(
                    persistentKey,
                    npc.Id,
                    InitiativeDriver.Relational,
                    MemoryUrgency.Normal,
                    $"Trusts {name} — may confide, cooperate, or seek their counsel.",
                    45));
            }
        }

        return candidates;
    }

    private static InitiativeCandidate BuildAffectionCandidate(
        Character npc,
        string targetId,
        IReadOnlyDictionary<string, Character> presentById,
        string key,
        double weight,
        string framing)
    {
        var name = presentById.TryGetValue(targetId, out var target) ? target.Name : "someone present";
        return new InitiativeCandidate(
            key,
            npc.Id,
            InitiativeDriver.Relational,
            MemoryUrgency.Normal,
            framing.Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
                .Replace("someone present", name, StringComparison.OrdinalIgnoreCase),
            weight);
    }

    private static void AddCandidate(
        List<InitiativeCandidate> candidates,
        HashSet<string> seenKeys,
        InitiativeCandidate candidate)
    {
        if (seenKeys.Add(candidate.Key))
        {
            candidates.Add(candidate);
        }
    }
}