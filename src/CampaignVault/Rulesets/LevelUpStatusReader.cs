using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>Reads, from the campaign's XP rules and a character's XP, whether the next level is available and earned.</summary>
public static class LevelUpStatusReader
{
    public const int MaxLevel = 20;

    public static LevelUpStatus? For(Character character, CampaignConfig config, ProgressionDefinitionProvider? progressions)
    {
        if (!character.IsPc && !character.IsPartyCompanion)
            return null;

        var level = XpThresholdCalculator.GetCurrentLevel(character);
        var hasXpRule = config.XpProgression != XpProgressionType.Milestone
                        && (XpThresholdCalculator.HasXpTable(config.ActiveSystem) || config.CustomXpThresholds is { Count: > 0 });
        var target = level + 1;
        var needed = hasXpRule ? XpThresholdCalculator.GetXpForLevel(config.ActiveSystem, target, config.XpProgression, config.CustomXpThresholds) : (int?)null;
        var hasClass = CharacterClassResolver.ResolveClassLevels(character).Count > 0;
        return new LevelUpStatus
        {
            Possible = hasClass && level < MaxLevel,
            Ready = hasClass && XpThresholdCalculator.CanLevelUp(config.ActiveSystem, level, character.ExperiencePoints, config.XpProgression, config.CustomXpThresholds),
            Level = level,
            TargetLevel = target,
            Xp = character.ExperiencePoints,
            XpNeeded = needed,
        };
    }
}
