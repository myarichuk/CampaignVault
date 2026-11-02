using CampaignVault.Data.Context;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Crowd draw (WITNESS_CHANNEL_PLAN.md S1–S2): a beat that pulls attention (a song, a shouting match, loud haggling) gets an
/// engine-rolled chance that someone from the location's crowd comes over, instead of relying on the narrator to remember to
/// call scene_interrupt_check. The LLM can tilt the roll with crowdModifier; the resolver's own 1–75% clamp still applies.
/// </summary>
public static class CrowdDraw
{
    /// <summary>Engine modifier for a beat that draws attention, and for a merely loud one in a dense crowd (0.5% per point).</summary>
    public const int DrawModifier = 20;
    public const int LoudInCrowdModifier = 10;
    public const int MaxLlmModifier = 50;

    public sealed record Beat(string ActorId, string Cause, bool Draws, int LlmModifier, string? LocationId)
    {
        public int EngineModifier => Draws ? DrawModifier : LoudInCrowdModifier;
        public int TotalModifier => EngineModifier + LlmModifier;
    }

    /// <summary>
    /// A Performance check draws attention unless the action says <c>attention: notice</c>. Its whole purpose is being watched,
    /// and the skill list isn't generated data, so this one mapping stays in code.
    /// </summary>
    public static bool IsPerformance(RulesetAction action) =>
        action.ActionType is RulesetActionType.SkillCheck or RulesetActionType.ContestedCheck
        && (string.Equals(action.Parameters.GetValueOrDefault("skill")?.Trim(), "performance", StringComparison.OrdinalIgnoreCase)
            || action.ActionName.Contains("perform", StringComparison.OrdinalIgnoreCase));

    public static bool Draws(RulesetAction action)
    {
        var attention = action.Parameters.GetValueOrDefault("attention")?.Trim();
        return string.Equals(attention, "draw", StringComparison.OrdinalIgnoreCase)
               || (IsPerformance(action) && !string.Equals(attention, "notice", StringComparison.OrdinalIgnoreCase));
    }

    public static bool Draws(EventOccurred ev) => string.Equals(ev.Attention?.Trim(), "draw", StringComparison.OrdinalIgnoreCase);

    /// <summary>What an attention-drawing action or event gives off when it doesn't say: a draw is seen and heard.</summary>
    public static Perceptible Signal(string? perceptible, bool draws)
    {
        var parsed = Perceptibility.Parse(perceptible);
        return parsed == Perceptible.None && draws ? Perceptible.Both : parsed;
    }

    /// <summary>
    /// The batch's strongest crowd beat among its perceptible beats (<see cref="PerceptibleBeats"/>): a draw beats a merely loud one;
    /// null when nothing pulls a crowd. A loud spell counts (a fireball in a packed market), a silent or merely visible beat doesn't.
    /// </summary>
    public static Beat? Strongest(IEnumerable<PerceptibleBeat> beats)
    {
        Beat? best = null;
        foreach (var b in beats.Where(b => b.Draws || (b.Signal & Perceptible.Loud) != 0))
        {
            var beat = new Beat(b.ActorId, b.Cause, b.Draws, Clamp(b.CrowdModifier), b.LocationId);
            if (best == null || (beat.Draws && !best.Draws))
            {
                best = beat;
            }
        }

        return best;
    }

    /// <summary>The LLM's crowdModifier on an action, unclamped (clamped when the beat is ranked).</summary>
    public static int ModifierOf(RulesetAction action) => ParseInt(action.Parameters.GetValueOrDefault("crowdModifier")) ?? 0;

    /// <summary>The directive notes: who drew the crowd and how to cast whoever steps out.</summary>
    public static string Notes(Beat beat, string actorName) =>
        $"Drawn by {actorName}: {beat.Cause}. Fit who steps out to how it went (see this beat's check result, if any): "
        + "a well-received performance draws admirers, tippers or a rival; a poor one hecklers; a loud quarrel or haggling "
        + "draws a busybody, the watch, or a pickpocket using the distraction.";

    private static int Clamp(int? value) => Math.Clamp(value ?? 0, -MaxLlmModifier, MaxLlmModifier);

    private static int? ParseInt(string? value) => int.TryParse(value?.Trim().TrimStart('+'), out var n) ? n : null;
}
