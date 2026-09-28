using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Willpower recovers only from what wore it down. A drop through <c>attribute willpower</c> with a negative delta (or a
/// plugin's drain) is recorded in <see cref="SystemExtension.WillpowerDrained"/>, and each rest step gives back a little of
/// it. A value the DM sets outright is a new baseline and owes nothing, so rest never pushes a hardened 40 back to 75.
/// </summary>
public static class WillpowerRules
{
    /// <summary>Willpower regained per rest step (rests are stepped in 4 hour buckets).</summary>
    public const float RestPerStep = 5f;

    /// <summary>Applies a change from the <c>attribute</c> verb and keeps the owed amount in step. Returns the new willpower.</summary>
    public static float Apply(SystemExtension stats, float value, bool isDelta)
    {
        var before = stats.Willpower;
        var after = Math.Clamp(isDelta ? before + value : value, 0f, 100f);
        var change = after - before;
        if (!isDelta)
            stats.WillpowerDrained = 0f; // a set is the new normal
        else if (change < 0)
            stats.WillpowerDrained += -change;
        else if (change > 0)
            stats.WillpowerDrained = Math.Max(0f, stats.WillpowerDrained - change);
        stats.Willpower = after;
        return after;
    }

    /// <summary>Lowers willpower as something recoverable (captivity, an ordeal). Returns how much was actually lost.</summary>
    public static float Drain(SystemExtension stats, float amount)
    {
        var lost = Math.Min(Math.Max(0f, amount), stats.Willpower);
        stats.Willpower -= lost;
        stats.WillpowerDrained += lost;
        return lost;
    }

    /// <summary>One rest step: gives back up to <see cref="RestPerStep"/> of what was drained. Returns the amount restored.</summary>
    public static float RestStep(SystemExtension stats)
    {
        var back = Math.Min(RestPerStep, stats.WillpowerDrained);
        if (back <= 0)
            return 0f;
        stats.Willpower = Math.Min(100f, stats.Willpower + back);
        stats.WillpowerDrained -= back;
        return back;
    }
}
