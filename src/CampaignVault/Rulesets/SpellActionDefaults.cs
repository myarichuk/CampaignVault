using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// Fills a spell <c>ruleset_action</c>'s action-economy parameters from the spell definition when the caller didn't set them:
/// dnd5e bonus-action spells spend the bonus action, reaction spells are reactions, pf2e spells cost their listed actions,
/// and touch spells are melee attacks. Explicit parameters always win, so a DM can still overrule the data.
/// </summary>
public static class SpellActionDefaults
{
    public const string AttackTypeParameter = "attackType";
    public const string ActionCostParameter = "actionCost";
    public const string BonusActionParameter = "bonusAction";

    public const string CastKindAction = "action";
    public const string CastKindBonus = "bonus";
    public const string CastKindReaction = "reaction";
    public const string CastKindLong = "long";

    public static SpellDefinition? Apply(RulesetAction action, SpellDefinitionProvider? spells, string system)
    {
        if (action.ActionType != RulesetActionType.Spell || spells is null || string.IsNullOrWhiteSpace(action.ActionName))
        {
            return null;
        }

        var slug = action.ActionName.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (!spells.TryGet(system, slug, out var spell) || spell is null)
        {
            return null;
        }

        Apply(action, spell, system);
        return spell;
    }

    public static void Apply(RulesetAction action, SpellDefinition spell, string system)
    {
        var kind = spell.CastKind?.Trim().ToLowerInvariant();
        if (kind == CastKindReaction)
        {
            action.IsReaction = true;
        }
        else if (kind == CastKindBonus && string.Equals(system, RulesetSystem.Dnd5e, StringComparison.OrdinalIgnoreCase))
        {
            action.Parameters.TryAdd(BonusActionParameter, "true");
        }

        if (spell.ActionCost is { } cost && string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase)
            && !action.IsReaction)
        {
            action.Parameters.TryAdd(ActionCostParameter, cost.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(spell.AttackType))
        {
            action.Parameters.TryAdd(AttackTypeParameter, spell.AttackType.Trim().ToLowerInvariant());
        }
    }

    /// <summary>A spell with a casting time of a minute or more can't be cast within one combat turn.</summary>
    public static bool TooLongForCombat(SpellDefinition spell) =>
        string.Equals(spell.CastKind?.Trim(), CastKindLong, StringComparison.OrdinalIgnoreCase);
}
