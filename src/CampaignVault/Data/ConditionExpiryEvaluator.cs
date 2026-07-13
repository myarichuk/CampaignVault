using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data;

/// <summary>
/// Shared condition-expiry decisions for <see cref="StatusExpiryRule"/> and <see cref="ChangeHandlers.RestChangeHandler"/>.
/// </summary>
public static class ConditionExpiryEvaluator
{
    public static ConditionDefinition? TryResolve(
        ConditionDefinitionProvider? provider,
        SystemExtension stats,
        string? conditionName)
    {
        if (provider == null || string.IsNullOrWhiteSpace(conditionName))
            return null;

        if (!RulesetSystemResolver.TryFromStats(stats, out var system))
            return null;

        return provider.TryGet(system, conditionName, out var def) ? def : null;
    }

    /// <summary>
    /// Day-based expiry via <see cref="StatusEffect.ExpiresAtDay"/>.
    /// Unknown or legacy effects (no resolved definition) keep the historical behavior.
    /// </summary>
    public static bool ShouldExpireByElapsedDay(
        StatusEffect effect,
        ConditionDefinition? definition,
        float totalDaysElapsed)
    {
        if (!effect.ExpiresAtDay.HasValue || effect.ExpiresAtDay.Value > totalDaysElapsed)
            return false;

        if (definition == null)
            return true;

        return definition.DurationType == ConditionDurationType.Timed;
    }

    /// <summary>
    /// Clears at the next dawn after world time advances at least one day.
    /// No shipped SRD condition YAML uses this — only custom templates the LLM may author.
    /// </summary>
    public static bool ShouldExpireAtDawn(
        StatusEffect effect,
        ConditionDefinition? definition,
        double daysPassed)
    {
        if (daysPassed <= 0 || definition == null)
            return false;

        return definition.DurationType == ConditionDurationType.UntilDawn;
    }

    public static bool ShouldExpireOnLongRest(StatusEffect effect, ConditionDefinition? definition)
    {
        if (definition == null)
            return false;

        return definition.DurationType == ConditionDurationType.UntilLongRest;
    }

    // Concentration break checks (damage → CON save; recasting another concentration effect)
    // are handled inline in HpChangeHandler/StatusChangeHandler via StatusEffect.Name matching,
    // not here — no shipped condition YAML currently sets DurationType.Concentration.

    /// <summary>
    /// UntilLongRest effects that fully clear on long rest (non-stacking conditions).
    /// </summary>
    public static IReadOnlyList<StatusEffect> CollectLongRestFullClears(
        Character character,
        ConditionDefinitionProvider? provider)
    {
        if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
            return [];

        return character.SystemStats.StatusEffects
            .Where(e =>
            {
                var def = TryResolve(provider, character.SystemStats, e.ConditionName);
                return ShouldExpireOnLongRest(e, def) && !(def?.IsStacking ?? false);
            })
            .ToList();
    }

    /// <summary>
    /// UntilLongRest effects that decrement by one level on long rest instead of fully
    /// clearing (e.g. dnd5e exhaustion, tracked as "Exhaustion N" in StatusEffect.Name).
    /// </summary>
    public static IReadOnlyList<StatusEffect> CollectLongRestDecrements(
        Character character,
        ConditionDefinitionProvider? provider)
    {
        if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
            return [];

        return character.SystemStats.StatusEffects
            .Where(e =>
            {
                var def = TryResolve(provider, character.SystemStats, e.ConditionName);
                return ShouldExpireOnLongRest(e, def) && (def?.IsStacking ?? false);
            })
            .ToList();
    }

    public static IReadOnlyList<StatusEffect> CollectDawnExpirations(
        Character character,
        ConditionDefinitionProvider? provider,
        double daysPassed)
    {
        if (daysPassed <= 0 || character.SystemStats?.StatusEffects == null)
            return [];

        return character.SystemStats.StatusEffects
            .Where(e => ShouldExpireAtDawn(
                e,
                TryResolve(provider, character.SystemStats, e.ConditionName),
                daysPassed))
            .ToList();
    }
}