using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Decays memory salience over simulation ticks and bumps urgency on stale, still-salient memories.
/// Computes changes locally (never mutates the tracked Character/MemoryNode directly) and emits a
/// batched MemoryDecay delta per NPC, applied later by MemoryDecayHandler through the unified Commit
/// path — the same characters are already tracked by the session used for the Commit stage, so
/// mutating them here directly would double-apply the decay.
/// </summary>
public class MemorySalienceDecayRule : ISimulationRule
{
    public string Name => "Memory Salience Decay";
    public int Order => 46;

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var deltas = new List<WorldChange>();
        var days = (float)context.DaysPassed;
        var currentDay = context.Time.TotalDaysElapsed;
        var decayDays = context.Config?.MemoryImportantDecayDays ?? 40;
        var staleThreshold = Math.Max(1, decayDays / 2);

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Psychology?.Memories is not { Count: > 0 } memories)
            {
                continue;
            }

            var entryChanges = new Dictionary<string, (float? NewSalience, float? NewUrgency, bool Evict)>();
            var projectedSalience = new Dictionary<string, double>();

            foreach (var memory in memories.Values)
            {
                // Idempotent one-time fix-up for legacy zero-value documents; harmless to leave as a
                // direct mutation since it only ever fires once (Salience > 0 short-circuits it after).
                memory.ApplyMigrationDefaultsIfNeeded();

                var decayPerDay = memory.Importance switch
                {
                    MemoryImportance.Trivial => 0.08f,
                    MemoryImportance.Important => 0.05f,
                    MemoryImportance.Core => 0.02f,
                    _ => 0.05f
                };

                var floor = memory.Importance == MemoryImportance.Core ? 0.3 : 0.1;
                var before = memory.Salience;
                var after = Math.Max(floor, before - (decayPerDay * days));
                projectedSalience[memory.Topic] = after;

                MemoryUrgency? bumpedUrgency = null;
                var age = currentDay - memory.DayAcquired;
                if (before > 0.6 && age > staleThreshold && memory.Urgency < MemoryUrgency.High)
                {
                    bumpedUrgency = MemoryUrgency.High;
                }

                var salienceChanged = Math.Abs(after - before) > 0.0001;
                if (salienceChanged || bumpedUrgency.HasValue)
                {
                    entryChanges[memory.Topic] = (
                        salienceChanged ? (float)after : null,
                        bumpedUrgency.HasValue ? (float)(int)bumpedUrgency.Value : null,
                        false);
                }

                if (before - after > 0.01)
                {
                    narratives.Add(new RuleNarrative($"Memory '{memory.Topic}' for {npc.Name} is fading (salience {after:F2}).", Persist: false));
                }
            }

            // 4a: Evict non-Core memories at floor salience and cap total memory count
            var maxMemories = 40;
            var memoryFloor = 0.1f;

            // Flag non-Core memories that have decayed to floor salience for eviction
            var evictedTopics = new HashSet<string>();
            foreach (var memory in memories.Values)
            {
                if (memory.Importance == MemoryImportance.Core) continue;
                var projected = projectedSalience.GetValueOrDefault(memory.Topic, memory.Salience);
                if (Math.Abs((float)projected - memoryFloor) < 0.01f)
                {
                    entryChanges[memory.Topic] = (null, null, true);
                    evictedTopics.Add(memory.Topic);
                }
            }

            // If still over cap after floor eviction, evict lowest-salience non-Core memories until under cap
            var remainingCount = memories.Count - evictedTopics.Count;
            if (remainingCount > maxMemories)
            {
                var nonCoreMemories = memories.Values
                    .Where(m => m.Importance != MemoryImportance.Core && !evictedTopics.Contains(m.Topic))
                    .OrderBy(m => projectedSalience.GetValueOrDefault(m.Topic, m.Salience))
                    .ToList();

                var toRemove = remainingCount - maxMemories;
                for (int i = 0; i < toRemove && i < nonCoreMemories.Count; i++)
                {
                    entryChanges[nonCoreMemories[i].Topic] = (null, null, true);
                }
            }

            if (entryChanges.Count > 0)
            {
                deltas.Add(new MemoryDecay
                {
                    CharacterId = npc.Id,
                    EntryChanges = entryChanges,
                });
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
