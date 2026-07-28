namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>Standard 5e skill → governing ability mapping (PHB skill list). Used to derive SkillModifiers.</summary>
internal static class Dnd5eSkillTable
{
    public static readonly IReadOnlyDictionary<string, string> GoverningAbility =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Acrobatics"] = "Dexterity",
            ["Animal Handling"] = "Wisdom",
            ["Arcana"] = "Intelligence",
            ["Athletics"] = "Strength",
            ["Deception"] = "Charisma",
            ["History"] = "Intelligence",
            ["Insight"] = "Wisdom",
            ["Intimidation"] = "Charisma",
            ["Investigation"] = "Intelligence",
            ["Medicine"] = "Wisdom",
            ["Nature"] = "Intelligence",
            ["Perception"] = "Wisdom",
            ["Performance"] = "Charisma",
            ["Persuasion"] = "Charisma",
            ["Religion"] = "Intelligence",
            ["Sleight of Hand"] = "Dexterity",
            ["Stealth"] = "Dexterity",
            ["Survival"] = "Wisdom",
        };
}
