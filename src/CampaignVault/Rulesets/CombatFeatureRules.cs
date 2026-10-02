using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// SRD class features the engine applies by itself, read from what the sheet records (class levels, recorded level-up
/// choices, feats). Deliberately data-light: the rules are code, the character supplies only the facts.
/// </summary>
internal static class CombatFeatureRules
{
    public static string Norm(string? value) =>
        value is null ? "" : string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    public static bool HasFightingStyle(SystemExtension stats, string style) =>
        stats.LevelUpChoices.Any(c => Norm(c.Key) == "fightingstyle" && Norm(c.Value) == Norm(style));

    public static bool HasFeat(SystemExtension stats, string feat)
    {
        var wanted = Norm(feat);
        var listed = stats switch
        {
            Dnd5eExtension d => d.Feats,
            _ => [],
        };
        return listed.Any(f => Norm(f) == wanted) || stats.LevelUpChoices.Any(c => Norm(c.Value) == wanted);
    }

    public static bool IsTrue(RulesetAction action, string key) =>
        action.Parameters.TryGetValue(key, out var v) && bool.TryParse(v, out var b) && b;

    /// <summary>A weapon is light when its item tags or its weaponTags property say so.</summary>
    public static bool IsLightWeapon(Item weapon) =>
        weapon.Tags.Any(t => Norm(t) == "light")
        || (weapon.Properties.TryGetValue("weaponTags", out var raw)
            && (raw?.ToString() ?? "").Split(',').Any(t => Norm(t) == "light"));

    /// <summary>The sheet's proficiency bonus: the stored attribute, else derived from level, else 0.</summary>
    public static int ProficiencyBonus(Dnd5eExtension stats) =>
        stats.Attributes.TryGetValue("proficiencyBonus", out var p) ? (int)p
        : stats.Level is > 0 ? Bootstrap.Dnd5eClassProfileResolver.ProficiencyBonus(stats.Level.Value)
        : 0;

    /// <summary>
    /// 5e: whether the attack's weapon is one the sheet's <c>weaponProficiencies</c> cover, by category (<c>simple</c>,
    /// <c>martial</c>, from the item's tags) or by item name. An empty list, or a weapon of unknown category and name,
    /// counts as proficient: characters from before the list existed keep their bonus.
    /// </summary>
    public static bool IsWeaponProficient(Dnd5eExtension stats, RulesetAction action)
    {
        if (stats.WeaponProficiencies.Count == 0)
            return true;

        var tags = action.Parameters.TryGetValue("weaponTags", out var rawTags)
            ? rawTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Norm).ToHashSet()
            : [];
        var category = tags.Contains("martial") ? "martial" : tags.Contains("simple") ? "simple" : null;
        var name = action.Parameters.TryGetValue("weaponDefinition", out var definition) ? Norm(definition) : null;
        if (category is null && string.IsNullOrEmpty(name))
            return true;

        var known = stats.WeaponProficiencies.Select(Norm).ToHashSet();
        return (category is not null && known.Contains(category)) || (!string.IsNullOrEmpty(name) && known.Contains(name));
    }

    public static int ClassLevel(Character character, string classSlug) =>
        CharacterClassResolver.GetClassLevel(CharacterClassResolver.ResolveClassLevels(character), classSlug);

    /// <summary>Attacks made by one Attack action (5e Extra Attack; different classes do not stack).</summary>
    public static int ExtraAttacksPerAction(Character character)
    {
        var fighter = ClassLevel(character, "fighter");
        var fighterCount = fighter >= 20 ? 4 : fighter >= 11 ? 3 : fighter >= 5 ? 2 : 1;
        var others = new[] { "barbarian", "paladin", "ranger", "monk" }.Any(c => ClassLevel(character, c) >= 5) ? 2 : 1;
        return Math.Max(fighterCount, others);
    }

    /// <summary>5e Sneak Attack damage dice: ceil(rogue level / 2) d6.</summary>
    public static int Dnd5eSneakAttackDice(Character character) => (ClassLevel(character, "rogue") + 1) / 2;

    /// <summary>PF2e rogue precision dice: 1d6, plus one at levels 5, 11 and 17.</summary>
    public static int Pf2eSneakAttackDice(Character character, int level) =>
        ClassLevel(character, "rogue") < 1 ? 0 : 1 + (level >= 5 ? 1 : 0) + (level >= 11 ? 1 : 0) + (level >= 17 ? 1 : 0);
}
