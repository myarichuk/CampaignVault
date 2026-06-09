using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Mirrors High/Urgent NPC initiatives from scene views into WorldPressure (Phase 10).
/// </summary>
public sealed class UrgentInitiativePressureContributor : IPressureContributor
{
    public const string GroupingKey = "NpcInitiative:Urgent";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 12;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.PresentNPCs == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var npc in ctx.Scene.PresentNPCs)
        {
            if (npc.ActiveInitiatives == null)
            {
                continue;
            }

            foreach (var initiative in npc.ActiveInitiatives)
            {
                if (initiative.Urgency < MemoryUrgency.High)
                {
                    continue;
                }

                pressures.Add(new WorldPressureItem(
                    PressureSeverity.NarrativePrompt,
                    npc.Id,
                    $"{npc.Name} — {initiative.FramingPrompt}",
                    GroupingKey));
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}