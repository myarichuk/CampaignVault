using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Decays memory salience over simulation ticks and bumps urgency on stale, still-salient memories.
/// </summary>
public class MemorySalienceDecayRule : ISimulationRule
{
    public string Name => "Memory Salience Decay";
    public int Order => 46;

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
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

            foreach (var memory in memories.Values)
            {
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
                memory.Salience = Math.Max(floor, memory.Salience - (decayPerDay * days));

                var age = currentDay - memory.DayAcquired;
                if (before > 0.6 && age > staleThreshold && memory.Urgency < MemoryUrgency.High)
                {
                    memory.Urgency = MemoryUrgency.High;
                }

                if (before - memory.Salience > 0.01)
                {
                    narratives.Add(new RuleNarrative($"Memory '{memory.Topic}' for {npc.Name} is fading (salience {memory.Salience:F2}).", Persist: false));
                }
            }

            // 4a: Evict non-Core memories at floor salience and cap total memory count
            var maxMemories = 40;
            var memoryFloor = 0.1f;

            // Remove non-Core memories that have sat at floor salience beyond a threshold
            var memoriesToEvict = memories.Values
                .Where(m => m.Importance != MemoryImportance.Core && Math.Abs(m.Salience - memoryFloor) < 0.01f)
                .ToList();

            foreach (var memory in memoriesToEvict)
            {
                memories.Remove(memory.Topic);
            }

            // If still over cap, evict lowest-salience non-Core memories until under cap
            if (memories.Count > maxMemories)
            {
                var nonCoreMemories = memories.Values.Where(m => m.Importance != MemoryImportance.Core)
                    .OrderBy(m => m.Salience)
                    .ToList();

                var toRemove = memories.Count - maxMemories;
                for (int i = 0; i < toRemove && i < nonCoreMemories.Count; i++)
                {
                    memories.Remove(nonCoreMemories[i].Topic);
                }
            }
        }

        return Task.FromResult(new RuleResult(narratives, []));
    }
}