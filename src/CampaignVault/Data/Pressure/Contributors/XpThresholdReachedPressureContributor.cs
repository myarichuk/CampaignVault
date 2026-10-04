using CampaignVault.Models;
using CampaignVault.Services;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

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

        if (!XpThresholdCalculator.HasXpTable(activeSystem) && customThresholds is not { Count: > 0 })
        {
            return []; // No XP table for this system (e.g. Narrative) and no custom thresholds
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

                // The class as the sheet names it ("Human Fighter 5" is Fighter); a multiclass character has to pick one.
                var classes = CharacterClassResolver.ResolveClassLevels(character)
                    .Select(e => Dnd5eClassProfileResolver.CanonicalClass(e.Class) is { } known ? char.ToUpperInvariant(known[0]) + known[1..] : e.Class)
                    .ToList();
                var suggestedClass = classes.FirstOrDefault() ?? "Fighter";
                var suggestedCommit = $$"""
                    {
                        "$type": "level_up",
                        "characterId": "{{character.Id}}",
                        "levelsGained": 1,
                        "classGained": "{{suggestedClass}}",
                        "reason": "Reached XP threshold for level {{nextLevel}}"
                    }
                    """;

                var message = $"{character.Name} (L{currentLevel}) has {xp} XP — enough to reach level {nextLevel} " +
                    $"(needed {xpRequired} XP). Call lookup kind:'level_up' characterId:'{character.Id}' " +
                    "to see its 'slots' (subclass, feat, ability improvement, PF2e boosts and skill increase) to talk through with the player, " +
                    "then commit level_up with their answers as 'picks' (slot id → option ids). If the player is at the Unity client they may already be choosing.";

                if (classes.Count > 1)
                {
                    message += $" {character.Name} has more than one class: set classGained to the one that gains the level ({string.Join(", ", classes)}).";
                }

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