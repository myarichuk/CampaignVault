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
    public static IReadOnlyDictionary<string, float> ComputeDeltas(CampaignConfig? config, double days, Dictionary<string, float>? accumulationRates = null)
    {
        var needRate = config?.NeedAccumulationRate ?? 10f;
        var thirstMult = config?.ThirstAccumulationMultiplier ?? 1.2f;
        var tiredMult = config?.TirednessAccumulationMultiplier ?? 0.8f;
        var amount = needRate * (float)days;

        var deltas = new Dictionary<string, float>
        {
            ["hunger"] = amount,
            ["thirst"] = amount * thirstMult,
            ["tiredness"] = amount * tiredMult,
            ["social_drive"] = amount * 0.15f
        };

        // Per-character custom rates: a key matching a core need overrides the config-driven
        // value for that character only; other keys add new drifting needs at rate * days.
        if (accumulationRates is not null)
        {
            foreach (var (need, rate) in accumulationRates)
            {
                deltas[need] = rate * (float)days;
            }
        }

        return deltas;
    }
}
