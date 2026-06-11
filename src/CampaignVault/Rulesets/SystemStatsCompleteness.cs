using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class SystemStatsCompleteness
{
    public static bool IsCombatant(Character character) =>
        character.KeepAlive || character.MaxHp > 0;

    public static bool IsComplete(Character character, RulesetSystem activeSystem)
    {
        if (!IsCombatant(character))
        {
            return true;
        }

        return activeSystem switch
        {
            RulesetSystem.Dnd5e => IsDnd5eComplete(character.SystemStats as Dnd5eExtension),
            RulesetSystem.Pathfinder2e => IsPf2eComplete(character.SystemStats as Pf2eExtension),
            RulesetSystem.Fallout2d20 => IsFalloutComplete(character.SystemStats as Fallout2d20Extension),
            _ => character.SystemStats is not SystemExtension || HasAnyCustomAttributes(character.SystemStats)
        };
    }

    public static IReadOnlyList<string> GetMissingFields(Character character, RulesetSystem activeSystem)
    {
        if (!IsCombatant(character) || IsComplete(character, activeSystem))
        {
            return [];
        }

        return activeSystem switch
        {
            RulesetSystem.Dnd5e => GetDnd5eMissing(character.SystemStats as Dnd5eExtension),
            RulesetSystem.Pathfinder2e => GetPf2eMissing(character.SystemStats as Pf2eExtension),
            RulesetSystem.Fallout2d20 => GetFalloutMissing(character.SystemStats as Fallout2d20Extension),
            _ => ["systemStats"]
        };
    }

    public static string BuildExampleCommit(Character character, RulesetSystem activeSystem)
    {
        var id = character.Id;
        var name = character.Name;

        return activeSystem switch
        {
            RulesetSystem.Dnd5e =>
                $$"""
                [ { "$type": "system_stats", "characterId": "{{id}}", "systemStats": { "$system": "dnd5e", "armorClass": 15, "dexterity": 14, "strength": 8, "skillModifiers": { "Stealth": 6, "Perception": 2 }, "savingThrowModifiers": { "Dexterity": 2 } } } ]
                """,
            RulesetSystem.Pathfinder2e =>
                $$"""
                [ { "$type": "system_stats", "characterId": "{{id}}", "systemStats": { "$system": "pf2e", "armorClass": 16, "dexterityMod": 2, "wisdomMod": 1, "skillModifiers": { "Perception": 7, "Athletics": 5 }, "savingThrowModifiers": { "Fortitude": 6, "Reflex": 7, "Will": 4 } } } ]
                """,
            RulesetSystem.Fallout2d20 =>
                $$"""
                [ { "$type": "system_stats", "characterId": "{{id}}", "systemStats": { "$system": "fallout2d20", "agility": 7, "perception": 6, "endurance": 5, "defense": 1, "skills": { "SmallGuns": 2, "Sneak": 1 }, "tagSkills": ["SmallGuns"], "damageResistance": { "Physical": 0 } } } ]
                """,
            _ =>
                $$"""[ { "$type": "system_stats", "characterId": "{{id}}", "systemStats": { "attributes": { "attackBonus": 4 } } } ]"""
        };
    }

    private static bool IsDnd5eComplete(Dnd5eExtension? stats)
    {
        if (stats is null)
        {
            return false;
        }

        if (stats.ArmorClass != 10)
        {
            return true;
        }

        if (stats.Strength != 10 || stats.Dexterity != 10 || stats.Constitution != 10
            || stats.Intelligence != 10 || stats.Wisdom != 10 || stats.Charisma != 10)
        {
            return true;
        }

        if (stats.SkillModifiers.Count > 0 || stats.SavingThrowModifiers.Count > 0)
        {
            return true;
        }

        return HasAnyCustomAttributes(stats);
    }

    private static bool IsPf2eComplete(Pf2eExtension? stats)
    {
        if (stats is null)
        {
            return false;
        }

        if (stats.ArmorClass != 10)
        {
            return true;
        }

        if (stats.StrengthMod != 0 || stats.DexterityMod != 0 || stats.ConstitutionMod != 0
            || stats.IntelligenceMod != 0 || stats.WisdomMod != 0 || stats.CharismaMod != 0)
        {
            return true;
        }

        if (stats.SkillModifiers.Count > 0 || stats.SavingThrowModifiers.Count > 0)
        {
            return true;
        }

        return HasAnyCustomAttributes(stats);
    }

    private static bool IsFalloutComplete(Fallout2d20Extension? stats)
    {
        if (stats is null)
        {
            return false;
        }

        if (stats.Strength != 5 || stats.Perception != 5 || stats.Endurance != 5
            || stats.Charisma != 5 || stats.Intelligence != 5 || stats.Agility != 5 || stats.Luck != 5)
        {
            return true;
        }

        if (stats.Defense != 1)
        {
            return true;
        }

        if (stats.Skills.Count > 0 || stats.TagSkills.Count > 0 || stats.DamageResistance.Count > 0)
        {
            return true;
        }

        return HasAnyCustomAttributes(stats);
    }

    private static bool HasAnyCustomAttributes(SystemExtension stats) => stats.Attributes.Count > 0;

    private static List<string> GetDnd5eMissing(Dnd5eExtension? stats)
    {
        var missing = new List<string>();
        if (stats is null)
        {
            missing.Add("systemStats ($system: dnd5e)");
            return missing;
        }

        if (stats.ArmorClass == 10)
        {
            missing.Add("armorClass");
        }

        if (stats.Strength == 10 && stats.Dexterity == 10 && stats.Constitution == 10
            && stats.Intelligence == 10 && stats.Wisdom == 10 && stats.Charisma == 10)
        {
            missing.Add("ability scores (strength/dexterity/etc.)");
        }

        if (stats.SkillModifiers.Count == 0)
        {
            missing.Add("skillModifiers (at least one relevant skill)");
        }

        return missing;
    }

    private static List<string> GetPf2eMissing(Pf2eExtension? stats)
    {
        var missing = new List<string>();
        if (stats is null)
        {
            missing.Add("systemStats ($system: pf2e)");
            return missing;
        }

        if (stats.ArmorClass == 10)
        {
            missing.Add("armorClass");
        }

        if (stats.StrengthMod == 0 && stats.DexterityMod == 0 && stats.ConstitutionMod == 0
            && stats.IntelligenceMod == 0 && stats.WisdomMod == 0 && stats.CharismaMod == 0)
        {
            missing.Add("ability modifiers (strengthMod/dexterityMod/etc.)");
        }

        if (!stats.SkillModifiers.ContainsKey("Perception")
            && !stats.SkillModifiers.Keys.Any(k => string.Equals(k, "Perception", StringComparison.OrdinalIgnoreCase)))
        {
            missing.Add("skillModifiers.Perception (initiative)");
        }

        return missing;
    }

    private static List<string> GetFalloutMissing(Fallout2d20Extension? stats)
    {
        var missing = new List<string>();
        if (stats is null)
        {
            missing.Add("systemStats ($system: fallout2d20)");
            return missing;
        }

        if (stats.Strength == 5 && stats.Perception == 5 && stats.Endurance == 5
            && stats.Charisma == 5 && stats.Intelligence == 5 && stats.Agility == 5 && stats.Luck == 5)
        {
            missing.Add("SPECIAL attributes");
        }

        if (stats.Defense == 1)
        {
            missing.Add("defense");
        }

        if (stats.Skills.Count == 0)
        {
            missing.Add("skills (at least one relevant skill)");
        }

        return missing;
    }
}