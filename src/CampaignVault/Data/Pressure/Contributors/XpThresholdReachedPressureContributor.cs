using CampaignVault.Models;
using CampaignVault.Services;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class XpThresholdReachedPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Character:XpThresholdReached";

    public PressureScope Scope => PressureScope.World;
    public int Order => 5;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        if (!ctx.Config.AutoLevelUpPrompt)
        {
            return [];
        }

        var activeSystem = ctx.Config.ActiveSystem;
        var progression = ctx.Config.XpProgression;
        var customThresholds = ctx.Config.CustomXpThresholds;

        if (progression == XpProgressionType.Milestone)
        {
            return []; // Milestone doesn't use XP thresholds
        }

        var characters = await PressureQueryHelper.QueryCombatantCharactersAsync(ctx.Session, ctx.CampaignName, 50, ct);
        var pressures = new List<WorldPressureItem>();

        foreach (var character in characters)
        {
            var currentLevel = XpThresholdCalculator.GetCurrentLevel(character);
            var xp = character.ExperiencePoints;

            if (XpThresholdCalculator.CanLevelUp(activeSystem, currentLevel, xp, progression, customThresholds))
            {
                var nextLevel = currentLevel + 1;
                var xpRequired = XpThresholdCalculator.GetXpForLevel(activeSystem, nextLevel, progression, customThresholds);
                var xpSurplus = xp - xpRequired;

                var suggestedCommit = $$"""
                    {
                        "$type": "level_up",
                        "characterId": "{{character.Id}}",
                        "levelsGained": 1,
                        "classGained": "{{character.ClassLevel?.Split('/')[0]?.Split(' ')[0] ?? "Fighter"}}",
                        "reason": "Reached XP threshold for level {{nextLevel}}"
                    }
                    """;

                var message = $"{character.Name} (L{currentLevel}) has {xp} XP — enough to reach level {nextLevel} " +
                    $"(needed {xpRequired} XP). Call get_rules_reference kind:'level_up' characterId:'{character.Id}' " +
                    "to see any choices (subclass, feat, ASI) to talk through with the player before committing.";

                if (xpSurplus > 0)
                {
                    message += $" Surplus: {xpSurplus} XP.";
                }

                pressures.Add(new WorldPressureItem(
                    PressureSeverity.NarrativePrompt,
                    character.Id,
                    message,
                    GroupingKey)
                {
                    SuggestedCommitJson = suggestedCommit,
                    Abbreviation = $"XP_THRESHOLD:L{nextLevel}"
                });
            }
        }

        return pressures;
    }
}