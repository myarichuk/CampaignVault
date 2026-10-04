using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// What conditions do to rolls, read from the condition data (<see cref="ConditionDefinition.Effects"/> and
/// <see cref="ConditionDefinition.Levels"/>). Both directions: the actor's own conditions (poisoned: disadvantage on attacks and
/// checks), and, for an attack, the target's <c>againstSelf</c> conditions (prone: melee attackers have advantage). An
/// <c>autoFailSave</c> sets <see cref="RollModifier.AutoFail"/>. A core provider: it runs for every ruleset that has condition
/// data and is not gated by the plugin system filter.
/// </summary>
internal sealed class ConditionMechanicsProvider : IRollModifierProvider
{
    private readonly ConditionDefinitionProvider? _conditions;

    public ConditionMechanicsProvider(ConditionDefinitionProvider? conditions = null)
    {
        _conditions = conditions;
    }

    public IEnumerable<RollModifier> Modifiers(RollQuery query)
    {
        if (_conditions is null || query.Kind is not (RollKinds.Attack or RollKinds.Check or RollKinds.Save or RollKinds.ArmorClass))
            return [];

        var result = new List<RollModifier>();
        foreach (var condition in ConditionMechanics.Active(query.Actor, _conditions, query.System))
        {
            foreach (var effect in condition.Effects.Where(e => !e.AppliesAgainstSelf))
                Apply(effect, condition, query, result, theirs: false);
        }

        // An attack is also affected by what the target's conditions do to attackers.
        if (query.Kind == RollKinds.Attack && query.Other is not null)
        {
            foreach (var condition in ConditionMechanics.Active(query.Other, _conditions, query.System))
            {
                foreach (var effect in condition.Effects.Where(e => e.AppliesAgainstSelf))
                    Apply(effect, condition, query, result, theirs: true);
            }
        }

        return FoldTypedBonuses(result);
    }

    /// <summary>
    /// Whether the target's conditions turn a hit into a critical hit: an <c>autoCrit</c> effect against the bearer, gated like any
    /// other by <c>range</c> (paralyzed and unconscious: attacks from within 5 feet, which is every melee attack).
    /// </summary>
    public bool AutoCrit(Character target, string? system, IReadOnlyCollection<string> attackTags) =>
        _conditions is not null
        && ConditionMechanics.Active(target, _conditions, system).Any(c => c.Effects.Any(e =>
            e.Kind == ConditionEffectKinds.AutoCrit && e.AppliesAgainstSelf
            && (e.Range is not { Length: > 0 } range || attackTags.Contains(range, StringComparer.OrdinalIgnoreCase))));

    /// <summary>
    /// Feet of movement after the bearer's conditions: a <c>speed</c> effect with <c>set</c> fixes it (the lowest wins; 0 stops
    /// the bearer) and each <c>multiplier</c> scales it (exhaustion 2 halves it). Never below 0.
    /// </summary>
    public int AdjustSpeed(Character character, string? system, int feet)
    {
        if (_conditions is null)
            return feet;

        var effects = ConditionMechanics.Active(character, _conditions, system)
            .SelectMany(c => c.Effects)
            .Where(e => e.Kind == ConditionEffectKinds.Speed && !e.AppliesAgainstSelf)
            .ToList();
        var result = (double)feet;
        foreach (var e in effects.Where(e => e.Multiplier is not null))
            result *= e.Multiplier!.Value;
        foreach (var e in effects.Where(e => e.Set is not null))
            result = Math.Min(result, e.Set!.Value);
        return Math.Max(0, (int)Math.Floor(result));
    }

    /// <summary>Whether the character has the named condition, directly or implied by another (see <see cref="ConditionMechanics.Has"/>).</summary>
    public bool Has(Character character, string? system, string condition) =>
        _conditions is not null && ConditionMechanics.Has(character, _conditions, system, condition);

    private static void Apply(ConditionEffect effect, ActiveCondition condition, RollQuery query, List<RollModifier> into, bool theirs)
    {
        if (!Matches(effect, query))
            return;

        var source = condition.Definition.Name;
        var owner = theirs ? "target is " : "";
        switch (effect.Kind)
        {
            case ConditionEffectKinds.Advantage:
                into.Add(new RollModifier(source, 0, AdvantageEffect.Advantage, $"advantage: {owner}{source}"));
                break;
            case ConditionEffectKinds.Disadvantage:
                into.Add(new RollModifier(source, 0, AdvantageEffect.Disadvantage, $"disadvantage: {owner}{source}"));
                break;
            case ConditionEffectKinds.Bonus when effect.Value is { } value:
            {
                var amount = effect.PerLevel ? value * condition.Level : value;
                if (amount != 0)
                {
                    var type = string.IsNullOrWhiteSpace(effect.BonusType) ? "untyped" : effect.BonusType.Trim().ToLowerInvariant();
                    into.Add(new RollModifier(TypedPrefix + type, amount, AdvantageEffect.None, source));
                }

                break;
            }
            case ConditionEffectKinds.AutoFailSave when query.Kind == RollKinds.Save
                && effect.Abilities.Any(a => ConditionMechanics.SameSubject(a, query.Subject)):
                into.Add(new RollModifier(source, 0, AdvantageEffect.None, $"automatic failure: {source}") { AutoFail = true });
                break;
        }
    }

    private const string TypedPrefix = "typed:";

    /// <summary>
    /// PF2e stacking among the conditions' own numbers: of one bonus type only the worst penalty and the best bonus count
    /// (two sources of off-guard are one -2); untyped numbers all add. Advantage and auto-fail entries pass through.
    /// </summary>
    private static List<RollModifier> FoldTypedBonuses(List<RollModifier> given)
    {
        var typed = given.Where(m => m.Source.StartsWith(TypedPrefix, StringComparison.Ordinal)).ToList();
        if (typed.Count == 0)
            return given;

        var result = given.Where(m => !m.Source.StartsWith(TypedPrefix, StringComparison.Ordinal)).ToList();
        foreach (var group in typed.GroupBy(m => m.Source))
        {
            if (group.Key == TypedPrefix + "untyped")
            {
                result.AddRange(group.Select(m => Explain(m, "")));
                continue;
            }

            var label = group.Key[TypedPrefix.Length..];
            var worst = group.Where(m => m.Bonus < 0).OrderBy(m => m.Bonus).FirstOrDefault();
            var best = group.Where(m => m.Bonus > 0).OrderByDescending(m => m.Bonus).FirstOrDefault();
            if (worst is not null)
                result.Add(Explain(worst, label));
            if (best is not null)
                result.Add(Explain(best, label));
        }

        return result;
    }

    private static RollModifier Explain(RollModifier m, string type) =>
        new(m.Reason, m.Bonus, AdvantageEffect.None, $"{m.Bonus:+#;-#}{(type.Length > 0 ? " " + type : "")}: {m.Reason}");

    private static bool Matches(ConditionEffect effect, RollQuery query)
    {
        if (effect.Kind is not (ConditionEffectKinds.Advantage or ConditionEffectKinds.Disadvantage or ConditionEffectKinds.AutoFailSave
            or ConditionEffectKinds.Bonus))
            return false;

        if (effect.Kind != ConditionEffectKinds.AutoFailSave
            && !string.Equals(effect.On, query.Kind, StringComparison.OrdinalIgnoreCase))
            return false;

        if (effect.Subject is { Length: > 0 } subject && !ConditionMechanics.SameSubject(subject, query.Subject))
            return false;

        if (effect.Range is { Length: > 0 } range && !query.Tags.Contains(range, StringComparer.OrdinalIgnoreCase))
            return false;

        return effect.RequiresTag is not { Length: > 0 } tag || query.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }
}
