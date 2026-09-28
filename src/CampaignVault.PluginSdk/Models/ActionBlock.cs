namespace CampaignVault.Models;

/// <summary>
/// Marks a WorldChange verb as an action its actor takes (0.9.0): the actor is the change's <c>ActorId</c>, else its
/// <c>CharacterId</c>. The host refuses it at top level while that actor is <see cref="ActionBlock.IsBlocked">blocked</see>
/// (incapacitated, stunned, paralyzed, ... or carrying a <see cref="ActionBlock.Tag"/> status). Opt-in: a verb without
/// it is never blocked, which is what you want for verbs aimed at someone, saves, and recovery from the block itself.
/// Engine follow-ups (event reactions) are never blocked.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ActorActionAttribute : Attribute;

/// <summary>
/// The one definition of "cannot act". Core conditions are recognised by name; anything else (a plugin's own condition,
/// a homebrew effect) opts in by carrying <see cref="Tag"/> in its <see cref="StatusEffect.StatModifiers"/>, so the host
/// never has to learn a plugin's condition names. Whoever applies a blocking effect must also give it an exit: a duration
/// (<see cref="StatusEffect.ExpiresAtDay"/> / <see cref="StatusEffect.ExpiresAtRound"/> /
/// <see cref="StatusEffect.ExpiresAtOwnTurnStart"/>) or an owner that removes it, in and out of encounters.
/// </summary>
public static class ActionBlock
{
    /// <summary>StatModifiers key: a non-zero value blocks every action by the bearer.</summary>
    public const string Tag = "BlocksAllActions";

    /// <summary>Standard conditions that prevent taking any action at all, in either system.</summary>
    public static readonly IReadOnlySet<string> Conditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "incapacitated", "paralyzed", "petrified", "stunned", "unconscious",
    };

    /// <summary>
    /// The first status effect that stops <paramref name="character"/> acting, or null. With <paramref name="nowDays"/>
    /// (<c>TotalDaysElapsed + Hour / 24.0</c>), an effect whose <see cref="StatusEffect.ExpiresAtDay"/> has passed no longer
    /// blocks even if nothing has removed it yet, so a timed block always has an exit.
    /// </summary>
    public static StatusEffect? Blocker(Character? character, double? nowDays = null) =>
        character?.SystemStats?.StatusEffects?.FirstOrDefault(e =>
            ((e.Name is not null && Conditions.Contains(e.Name)) ||
             (e.ConditionName is not null && Conditions.Contains(e.ConditionName)) ||
             (e.StatModifiers.TryGetValue(Tag, out var v) && v != 0)) &&
            !(nowDays is { } now && e.ExpiresAtDay is { } expires && expires <= now));

    public static bool IsBlocked(Character? character, out StatusEffect? blocker, double? nowDays = null)
    {
        blocker = Blocker(character, nowDays);
        return blocker is not null;
    }
}
