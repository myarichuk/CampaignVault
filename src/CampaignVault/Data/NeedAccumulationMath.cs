using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Shared per-day need accumulation rates, used by both <see cref="NeedsAccumulationRule"/> (the
/// day-tick sweep over every scheduled character) and <see cref="ChangeHandlers.WorldChangeDispatcher"/>'s
/// per-commit micro nudge (fractional days, scoped to characters involved in that commit). Keeping the
/// rates in one place means the two can't drift out of sync.
/// </summary>
public static class NeedAccumulationMath
{
    public static IReadOnlyDictionary<string, float> ComputeDeltas(CampaignConfig? config, double days)
    {
        var needRate = config?.NeedAccumulationRate ?? 10f;
        var thirstMult = config?.ThirstAccumulationMultiplier ?? 1.2f;
        var tiredMult = config?.TirednessAccumulationMultiplier ?? 0.8f;
        var amount = needRate * (float)days;

        return new Dictionary<string, float>
        {
            ["hunger"] = amount,
            ["thirst"] = amount * thirstMult,
            ["tiredness"] = amount * tiredMult,
            ["social_drive"] = amount * 0.15f
        };
    }
}
