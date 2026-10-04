using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampaignVault.Rulesets;

/// <summary>
/// Folds every <see cref="IRollModifierProvider"/> (core's own and the plugins') into one result per roll: numeric bonuses add,
/// any advantage and any disadvantage cancel, and each provider that explains itself adds a note. A provider that throws is
/// logged and skipped. With no plugin providers this is exactly the old status-effect fold.
/// </summary>
public sealed class RollModifierPipeline
{
    private readonly IReadOnlyList<IRollModifierProvider> _providers;
    private readonly ILogger _logger;

    public RollModifierPipeline(IEnumerable<IRollModifierProvider> providers, ILogger<RollModifierPipeline>? logger = null)
    {
        // Core's numeric layer first so its (unexplained) sum leads the notes; a duplicate registration is dropped.
        var list = providers.ToList();
        if (!list.Any(p => p is StatusEffectModifierProvider))
            list.Insert(0, new StatusEffectModifierProvider());
        if (!list.Any(p => p is WillpowerModifierProvider))
            list.Insert(1, new WillpowerModifierProvider());
        _providers = list.OrderBy(p => p is StatusEffectModifierProvider ? 0 : p is WillpowerModifierProvider ? 1 : p is ConditionMechanicsProvider ? 2 : 3).ToList();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Core's providers only: what a resolver built without the container uses.</summary>
    public static RollModifierPipeline BuiltIn { get; } = new([]);

    public RollResolution Resolve(RollQuery query, int baseBonus, AdvantageEffect explicitAdvantage = AdvantageEffect.None, bool alsoAdvantage = false, bool alsoDisadvantage = false)
    {
        var bonus = baseBonus;
        var advantage = explicitAdvantage == AdvantageEffect.Advantage || alsoAdvantage;
        var disadvantage = explicitAdvantage == AdvantageEffect.Disadvantage || alsoDisadvantage;
        var notes = new List<string>();
        var autoFail = false;

        foreach (var provider in _providers)
        {
            if (provider is not (StatusEffectModifierProvider or WillpowerModifierProvider or ConditionMechanicsProvider) &&
                !PluginSystems.AppliesTo(provider, query.System))
                continue;

            List<RollModifier> given;
            try
            {
                // Concentration and death saves are saves to core's own layers; a plugin provider sees the specific kind.
                var asked = query.Kind is RollKinds.ConcentrationSave or RollKinds.DeathSave
                    && provider is StatusEffectModifierProvider or WillpowerModifierProvider or ConditionMechanicsProvider
                    ? query with { Kind = RollKinds.Save }
                    : query;
                given = provider.Modifiers(asked).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Roll modifier provider {Provider} failed; skipped.", provider.GetType().Name);
                continue;
            }

            foreach (var m in given)
            {
                bonus += m.Bonus;
                advantage |= m.Advantage == AdvantageEffect.Advantage;
                disadvantage |= m.Advantage == AdvantageEffect.Disadvantage;
                autoFail |= m.AutoFail;
                if (!string.IsNullOrWhiteSpace(m.Reason))
                    notes.Add(m.Reason);
            }
        }

        var net = advantage == disadvantage
            ? AdvantageEffect.None
            : advantage ? AdvantageEffect.Advantage : AdvantageEffect.Disadvantage;
        return new RollResolution(bonus, net, notes) { AutoFail = autoFail };
    }

    /// <summary>Whether a hit on <paramref name="target"/> is automatically a critical hit because of its conditions.</summary>
    public bool AutoCrit(Character target, string? system, IReadOnlyCollection<string> attackTags) =>
        _providers.OfType<ConditionMechanicsProvider>().Any(p => p.AutoCrit(target, system, attackTags));

    /// <summary>Whether <paramref name="character"/> has the condition, directly or implied by another (prone implies off-guard).</summary>
    public bool HasCondition(Character character, string? system, string condition) =>
        _providers.OfType<ConditionMechanicsProvider>().Any(p => p.Has(character, system, condition));

    /// <summary>
    /// What the character can actually move in a round: base speed, armour, every temporary modifier, then conditions (restrained
    /// and grappled set it to 0, exhaustion halves it). Can be 0. Use this for combat movement; <see cref="Speed"/> is the floored
    /// value travel needs.
    /// </summary>
    public int CombatSpeed(Character character, IReadOnlyDictionary<string, string> options, string? system, bool includeArmor = true)
    {
        var baseFeet = character.SystemStats?.Movement;
        if (baseFeet is null)
            return 0;
        var armor = includeArmor ? character.SystemStats!.MovementModifier : 0f;
        var query = new RollQuery(RollKinds.Speed, null, [], character, null, system, options);
        var resolved = Resolve(query, (int)Math.Floor(baseFeet.Value + armor));
        var feet = Math.Max(0, resolved.Bonus);
        foreach (var provider in _providers.OfType<ConditionMechanicsProvider>())
            feet = provider.AdjustSpeed(character, system, feet);
        return feet;
    }

    /// <summary>
    /// Movement in feet for travel: <see cref="CombatSpeed"/> floored at <see cref="SpeedRules.Floor"/> (never stuck at zero: a
    /// captive can still be dragged, so a trip never divides by zero).
    /// </summary>
    public int Speed(Character character, IReadOnlyDictionary<string, string> options, string? system, bool includeArmor = true)
    {
        if (character.SystemStats?.Movement is null)
            return 0;
        return Math.Max(SpeedRules.Floor, CombatSpeed(character, options, system, includeArmor));
    }
}
