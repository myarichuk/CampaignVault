using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class MemoryDecayPressureContributor : IPressureContributor
{
    public static string GetMemoryDecayGroupingKey(string npcId, string topic) => $"MemoryDecay:{npcId}:{topic}";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 40;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.PresentNPCs == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var npc in ctx.Scene.PresentNPCs)
        {
            if (npc.Memories == null)
            {
                continue;
            }

            foreach (var kv in npc.Memories)
            {
                var mem = kv.Value;
                if (mem.Importance == MemoryImportance.Core)
                {
                    continue;
                }

                var age = ctx.Time.TotalDaysElapsed - mem.DayAcquired;
                var threshold = mem.Importance == MemoryImportance.Important
                    ? ctx.Config.MemoryImportantDecayDays
                    : ctx.Config.MemoryTrivialDecayDays;

                if (age > threshold)
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, npc.Id,
                        $"Character '{npc.Name}' has a memory about '{mem.Topic}' that is {age:F0} days old and may be fading. " +
                        $"Consider misremembering, distorting, or forgetting details. Update it using `knowledge_update`.",
                        GetMemoryDecayGroupingKey(npc.Id, mem.Topic)));
                }
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}