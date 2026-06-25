using CampaignVault.Models;

namespace CampaignVault.Rulesets;

internal enum SpellResolutionMode
{
    Attack,
    Save,
    Check,
    Utility,
    Heal,
}

internal static class SpellResolutionHelper
{
    public static SpellResolutionMode InferMode(RulesetAction action)
    {
        if (TryGetParameter(action.Parameters, out var explicitMode, "resolution", "spellResolution"))
        {
            return ParseMode(explicitMode);
        }

        if (action.Parameters.ContainsKey("healDice")
            || (action.Parameters.TryGetValue("damageDice", out var dmg) && dmg.StartsWith('-')))
        {
            return SpellResolutionMode.Heal;
        }

        if (action.Parameters.ContainsKey("save") && action.Parameters.ContainsKey("dc"))
        {
            return SpellResolutionMode.Save;
        }

        if (TryGetParameter(action.Parameters, out _, "bonus", "toHitBonus", "spellAttackBonus")
            || (action.Parameters.ContainsKey("damageDice") && !action.Parameters.ContainsKey("dc")))
        {
            return SpellResolutionMode.Attack;
        }

        if (action.Parameters.ContainsKey("dc")
            && TryGetParameter(action.Parameters, out _, "skill", "ability"))
        {
            return SpellResolutionMode.Check;
        }

        if (action.ActionCategory is ActionCategory.Social or ActionCategory.Survival)
        {
            return SpellResolutionMode.Utility;
        }

        if (action.Parameters.ContainsKey("dc"))
        {
            return SpellResolutionMode.Check;
        }

        return SpellResolutionMode.Utility;
    }

    public static bool IsNonCombatMode(SpellResolutionMode mode) =>
        mode is SpellResolutionMode.Check or SpellResolutionMode.Utility or SpellResolutionMode.Heal;

    public static bool RequiresTargets(SpellResolutionMode mode, RulesetAction action)
    {
        if (mode is SpellResolutionMode.Attack or SpellResolutionMode.Save or SpellResolutionMode.Heal)
        {
            return true;
        }

        return action.TargetIds.Count > 0;
    }

    private static SpellResolutionMode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "attack" or "spellattack" or "spell_attack" => SpellResolutionMode.Attack,
        "save" or "savingthrow" or "saving_throw" or "spellsave" or "spell_save" => SpellResolutionMode.Save,
        "check" or "skillcheck" or "skill_check" => SpellResolutionMode.Check,
        "utility" or "narrative" or "exploration" or "social" => SpellResolutionMode.Utility,
        "heal" or "healing" or "recovery" => SpellResolutionMode.Heal,
        _ => SpellResolutionMode.Utility,
    };

    private static bool TryGetParameter(
        Dictionary<string, string> parameters,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}