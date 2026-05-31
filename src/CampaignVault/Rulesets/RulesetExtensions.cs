using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class RulesetExtensions
{
    /// <summary>
    /// Folds all active status modifiers matching the given tag into a base value.
    /// Also considers systemic values like Fatigue if applicable.
    /// </summary>
    public static int ApplyModifiers(this SystemExtension stats, string modifierTag, int baseValue)
    {
        float bonus = 0f;

        // Apply structured status effects
        if (stats.StatusEffects != null)
        {
            foreach (var effect in stats.StatusEffects)
            {
                if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue(modifierTag, out var mod))
                {
                    bonus += mod;
                }
                // Also check generic 'AllRolls' or 'AllChecks' tags if appropriate
                if (modifierTag != "AC" && modifierTag != "Defense") 
                {
                    if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue("AllRolls", out var allRollsMod))
                        bonus += allRollsMod;
                        
                    if (modifierTag.Contains("Skill") || modifierTag.Contains("Check"))
                    {
                        if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue("AllChecks", out var allChecksMod))
                            bonus += allChecksMod;
                    }
                }
            }
        }

        return baseValue + (int)Math.Floor(bonus);
    }
}
