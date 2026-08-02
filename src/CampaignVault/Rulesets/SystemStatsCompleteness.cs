using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class SystemStatsCompleteness
{
    public static bool IsCombatant(Character character) =>
        character.KeepAlive || character.MaxHp > 0;

    public static bool IsComplete(Character character, string activeSystem)
    {
        if (!IsCombatant(character))
        {
            return true;
        }

        return activeSystem switch
        {
            RulesetSystem.Dnd5e => IsDnd5eComplete(character.SystemStats as Dnd5eExtension),
            RulesetSystem.Pathfinder2e => IsPf2eComplete(character.SystemStats as Pf2eExtension),
            _ => character.SystemStats is not SystemExtension || HasAnyCustomAttributes(character.SystemStats)
        };
    }

    public static IReadOnlyList<string> GetMissingFields(Character character, string activeSystem)
    {
        if (!IsCombatant(character) || IsComplete(character, activeSystem))
        {
            return [];
        }

        return activeSystem switch
        {
            RulesetSystem.Dnd5e => GetDnd5eMissing(character.SystemStats as Dnd5eExtension),
            RulesetSystem.Pathfinder2e => GetPf2eMissing(character.SystemStats as Pf2eExtension),
            _ => ["systemStats"]
        };
    }

    public static string BuildExampleCommit(Character character, string activeSystem)
    {
        var id = character.Id;

        return activeSystem switch
        {
            RulesetSystem.Dnd5e =>
                $$"""
                [ { "$type": "character_update", "characterId": "{{id}}", "systemStats": { "$system": "dnd5e", "hitDie": "d10", "level": 2, "constitution": 14, "dexterity": 14, "skillModifiers": { "Athletics": 5, "Perception": 2 } } } ]
                """,
            RulesetSystem.Pathfinder2e =>
                $$"""
                [ { "$type": "character_update", "characterId": "{{id}}", "systemStats": { "$system": "pf2e", "classHpPerLevel": 10, "ancestryHp": 8, "level": 2, "constitutionMod": 2, "dexterityMod": 2, "skillModifiers": { "Perception": 7, "Athletics": 5 } } } ]
                """,
            _ =>
                $$"""[ { "$type": "character_update", "characterId": "{{id}}", "systemStats": { "attributes": { "attackBonus": 4 } } } ]"""
        };
    }

    public static string BuildStatBlockExampleCommit(Character character, string activeSystem)
    {
        var id = character.Id;

        return activeSystem switch
        {
            RulesetSystem.Dnd5e =>
                $$"""
                [ { "$type": "character_update", "characterId": "{{id}}", "systemStats": { "$system": "dnd5e", "statBlockHp": 7, "armorClass": 15, "dexterity": 14, "strength": 8, "skillModifiers": { "Stealth": 6, "Perception": 2 } } } ]
                """,
            RulesetSystem.Pathfinder2e =>
                $$"""
                [ { "$type": "character_update", "characterId": "{{id}}", "systemStats": { "$system": "pf2e", "statBlockHp": 20, "armorClass": 16, "dexterityMod": 2, "skillModifiers": { "Perception": 7 } } } ]
                """,
            _ =>
                BuildExampleCommit(character, activeSystem)
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

}