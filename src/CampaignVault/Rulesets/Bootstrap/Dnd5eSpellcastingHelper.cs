using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static class Dnd5eSpellcastingHelper
{
    private static readonly Dictionary<string, string> ClassSpellcastingAbility = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bard"] = "Charisma",
        ["cleric"] = "Wisdom",
        ["druid"] = "Wisdom",
        ["paladin"] = "Charisma",
        ["ranger"] = "Wisdom",
        ["sorcerer"] = "Charisma",
        ["warlock"] = "Charisma",
        ["wizard"] = "Intelligence",
    };

    public static string? InferSpellcastingAbility(IReadOnlyList<ClassLevelEntry> classLevels)
    {
        foreach (var entry in classLevels.OrderByDescending(c => c.Level).ThenBy(c => c.Class, StringComparer.Ordinal))
        {
            var ability = InferSpellcastingAbility(entry.Class);
            if (ability != null)
            {
                return ability;
            }
        }

        return null;
    }

    /// <summary>
    /// Longest-name-wins rather than first-match: a Dictionary's enumeration order is an implementation
    /// detail, so a first-match scan over a free-text class string that mentions two classes
    /// ("Cleric/Wizard", "Bard College of Whispers") would resolve to whichever entry happened to come
    /// first — a silently different spell save DC for the same character sheet. Ties break on the class
    /// name so the result is stable.
    /// </summary>
    public static string? InferSpellcastingAbility(string? classLevel)
    {
        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return null;
        }

        string? bestClass = null;
        string? bestAbility = null;

        foreach (var (className, ability) in ClassSpellcastingAbility)
        {
            if (!classLevel.Contains(className, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (bestClass == null
                || className.Length > bestClass.Length
                || (className.Length == bestClass.Length && string.CompareOrdinal(className, bestClass) < 0))
            {
                bestClass = className;
                bestAbility = ability;
            }
        }

        return bestAbility;
    }

    public static int GetAbilityModifier(Dnd5eExtension stats, string abilityName) =>
        abilityName.ToLowerInvariant() switch
        {
            "strength" => stats.GetAbilityModifier(stats.Strength),
            "dexterity" => stats.GetAbilityModifier(stats.Dexterity),
            "constitution" => stats.GetAbilityModifier(stats.Constitution),
            "intelligence" => stats.GetAbilityModifier(stats.Intelligence),
            "wisdom" => stats.GetAbilityModifier(stats.Wisdom),
            "charisma" => stats.GetAbilityModifier(stats.Charisma),
            _ => 0,
        };

    public static int ComputeSpellSaveDc(Dnd5eExtension stats, int proficiencyBonus, string? spellcastingAbility)
    {
        if (stats.SpellSaveDc is > 0)
        {
            return stats.SpellSaveDc.Value;
        }

        if (string.IsNullOrWhiteSpace(spellcastingAbility))
        {
            return 0;
        }

        return 8 + proficiencyBonus + GetAbilityModifier(stats, spellcastingAbility);
    }

    public static int ComputeSpellAttackBonus(Dnd5eExtension stats, int proficiencyBonus, string? spellcastingAbility)
    {
        if (stats.SpellAttackBonus is int attackBonus)
        {
            return attackBonus;
        }

        if (string.IsNullOrWhiteSpace(spellcastingAbility))
        {
            return 0;
        }

        return proficiencyBonus + GetAbilityModifier(stats, spellcastingAbility);
    }
}