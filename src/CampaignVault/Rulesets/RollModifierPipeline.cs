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
        _providers = list.OrderBy(p => p is StatusEffectModifierProvider ? 0 : p is WillpowerModifierProvider ? 1 : 2).ToList();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Core's providers only: what a resolver built without the container uses.</summary>
    public static RollModifierPipeline BuiltIn { get; } = new([]);

    public RollResolution Resolve(RollQuery query, int baseBonus, AdvantageEffect explicitAdvantage = AdvantageEffect.None)
    {
        var bonus = baseBonus;
        var advantage = explicitAdvantage == AdvantageEffect.Advantage;
        var disadvantage = explicitAdvantage == AdvantageEffect.Disadvantage;
        var notes = new List<string>();

        foreach (var provider in _providers)
        {
            if (provider is not (StatusEffectModifierProvider or WillpowerModifierProvider) &&
                !PluginSystems.AppliesTo(provider, query.System))
                continue;

            List<RollModifier> given;
            try
            {
                given = provider.Modifiers(query).ToList();
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
                if (!string.IsNullOrWhiteSpace(m.Reason))
                    notes.Add(m.Reason);
            }
        }

        var net = advantage == disadvantage
            ? AdvantageEffect.None
            : advantage ? AdvantageEffect.Advantage : AdvantageEffect.Disadvantage;
        return new RollResolution(bonus, net, notes);
    }

    /// <summary>Movement in feet after every temporary modifier, floored at 5 ft (never stuck at zero: a captive can still be dragged).</summary>
    public int Speed(Character character, IReadOnlyDictionary<string, string> options, string? system, bool includeArmor = true)
    {
        var baseFeet = character.SystemStats?.Movement;
        if (baseFeet is null)
            return 0;
        var armor = includeArmor ? character.SystemStats!.MovementModifier : 0f;
        var query = new RollQuery(RollKinds.Speed, null, [], character, null, system, options);
        var resolved = Resolve(query, (int)Math.Floor(baseFeet.Value + armor));
        return Math.Max(5, resolved.Bonus);
    }
}
