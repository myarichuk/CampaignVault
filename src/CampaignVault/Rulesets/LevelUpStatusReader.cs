using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// Reads whether a character's next level is available and earned: by the campaign's XP rule and the character's XP,
/// or because the DM granted it ahead (<c>level_up</c> with <c>earned: true</c>, the milestone way).
/// </summary>
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
        var possible = hasClass && level < MaxLevel;
        var earnedByXp = XpThresholdCalculator.CanLevelUp(config.ActiveSystem, level, character.ExperiencePoints, config.XpProgression, config.CustomXpThresholds);
        return new LevelUpStatus
        {
            Possible = possible,
            Ready = possible && (earnedByXp || character.PendingLevels > 0),
            Level = level,
            TargetLevel = target,
            Xp = character.ExperiencePoints,
            XpNeeded = needed,
        };
    }
}
