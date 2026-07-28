namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>PF2e skill → key ability mapping. Used to derive numeric SkillModifiers from proficiency ranks.</summary>
internal static class Pf2eSkillTable
{
    public static readonly IReadOnlyDictionary<string, string> KeyAbility =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Acrobatics"] = "Dexterity",
            ["Arcana"] = "Intelligence",
            ["Athletics"] = "Strength",
            ["Crafting"] = "Intelligence",
            ["Deception"] = "Charisma",
            ["Diplomacy"] = "Charisma",
            ["Intimidation"] = "Charisma",
            ["Medicine"] = "Wisdom",
            ["Nature"] = "Wisdom",
            ["Occultism"] = "Intelligence",
            ["Performance"] = "Charisma",
            ["Religion"] = "Wisdom",
            ["Society"] = "Intelligence",
            ["Stealth"] = "Dexterity",
            ["Survival"] = "Wisdom",
            ["Thievery"] = "Dexterity",
            ["Lore"] = "Intelligence",
        };

    public static readonly IReadOnlyDictionary<string, string> SaveKeyAbility =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fortitude"] = "Constitution",
            ["Reflex"] = "Dexterity",
            ["Will"] = "Wisdom",
        };
}
