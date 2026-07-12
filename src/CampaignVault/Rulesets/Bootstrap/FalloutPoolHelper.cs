using CampaignVault.Data;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static class FalloutPoolHelper
{
    public static bool IsTaggedSkill(Fallout2d20Extension stats, string skill) =>
        stats.TagSkills.Any(t => SkillNamesMatch(t, skill));

    public static int ResolvePoolSize(
        Fallout2d20Extension stats,
        IReadOnlyDictionary<string, string> parameters,
        string poolKey = "pool")
    {
        var poolSize = 2;
        if (parameters.TryGetValue(poolKey, out var poolStr) && int.TryParse(poolStr, out var explicitPool))
        {
            poolSize = explicitPool;
        }

        if (parameters.TryGetValue("bonusDice", out var bonusStr) && int.TryParse(bonusStr, out var bonusDice))
        {
            poolSize += Math.Max(0, bonusDice);
        }

        if (parameters.TryGetValue("useLuck", out var useLuckStr)
            && (useLuckStr.Equals("true", StringComparison.OrdinalIgnoreCase) || useLuckStr == "1")
            && stats.Attributes.TryGetValue("luckPoints", out var luckPoints)
            && luckPoints >= 1)
        {
            poolSize += 1;
        }

        return Math.Max(1, poolSize);
    }

    public static int ResolveAttackDifficulty(
        Fallout2d20Extension targetStats,
        IReadOnlyDictionary<string, string> parameters,
        Func<Fallout2d20Extension, int, string[], int> applyModifiers)
    {
        var defense = targetStats.Defense;
        defense = applyModifiers(targetStats, defense, ["Defense"]);

        if (parameters.TryGetValue("difficulty", out var diffStr) && int.TryParse(diffStr, out var explicitDifficulty))
        {
            return explicitDifficulty;
        }

        if (parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var dc))
        {
            return dc;
        }

        var modifier = 0;
        if (parameters.TryGetValue("rangeModifier", out var rangeStr) && int.TryParse(rangeStr, out var rangeMod))
        {
            modifier += rangeMod;
        }

        if (parameters.TryGetValue("cover", out var coverStr) && int.TryParse(coverStr, out var coverMod))
        {
            modifier += coverMod;
        }

        return Math.Max(0, defense + modifier);
    }

    /// <summary>
    /// Skill-name equality that ignores case AND whitespace, so content-authored names like
    /// "Small Guns" (human-readable, e.g. from weapons/*.yaml) reconcile with the engine's
    /// space-free key convention ("SmallGuns", e.g. character_create examples in DmHelpManual).
    /// </summary>
    private static bool SkillNamesMatch(string a, string b) =>
        string.Equals(
            a.Replace(" ", "", StringComparison.Ordinal),
            b.Replace(" ", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    public static int BuildTargetNumber(
        Fallout2d20Extension stats,
        string attribute,
        string skill,
        Func<Fallout2d20Extension, int, string[], int> applyModifiers,
        params string[] modifierTags)
    {
        var attrVal = GetAttributeValue(stats, attribute);
        var skillKey = stats.Skills.Keys.FirstOrDefault(k => SkillNamesMatch(k, skill));
        var skillVal = skillKey != null && stats.Skills.TryGetValue(skillKey, out var s) ? s : 0;
        var targetNumber = attrVal + skillVal;
        return applyModifiers(stats, targetNumber, modifierTags);
    }

    public static RollRequest BuildPoolRequest(
        Fallout2d20Extension stats,
        string attribute,
        string skill,
        IReadOnlyDictionary<string, string> parameters,
        string tag,
        Func<Fallout2d20Extension, int, string[], int> applyModifiers,
        params string[] modifierTags)
    {
        var poolKey = tag is "target" ? "targetPool" : "pool";
        var poolSize = ResolvePoolSize(stats, parameters, poolKey);
        var targetNumber = BuildTargetNumber(stats, attribute, skill, applyModifiers, modifierTags);
        var skillKey = stats.Skills.Keys.FirstOrDefault(k => SkillNamesMatch(k, skill));
        var skillVal = skillKey != null && stats.Skills.TryGetValue(skillKey, out var s) ? s : 0;

        return new RollRequest
        {
            Tag = tag,
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = IsTaggedSkill(stats, skill) ? skillVal : null,
        };
    }

    private static int GetAttributeValue(Fallout2d20Extension stats, string name) =>
        name.ToLowerInvariant() switch
        {
            "strength" => stats.Strength,
            "perception" => stats.Perception,
            "endurance" => stats.Endurance,
            "charisma" => stats.Charisma,
            "intelligence" => stats.Intelligence,
            "agility" => stats.Agility,
            "luck" => stats.Luck,
            _ => 5,
        };
}