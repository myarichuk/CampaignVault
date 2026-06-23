using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class FalloutDeriveHitPointsStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "fallout2d20.derive_hit_points";

    public bool CanApply(BootstrapContext context) =>
        !context.HasExplicitMaxHp
        && context.Character.SystemStats is Fallout2d20Extension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Fallout2d20Extension)context.Character.SystemStats;
        var level = Math.Max(1, stats.Level ?? ParseLevel(context.Character.ClassLevel) ?? 1);
        var hpPerLevel = stats.HpPerLevel ?? stats.Endurance;
        var maxHp = stats.Endurance + stats.Luck + (level - 1) * hpPerLevel;

        context.Character.MaxHp = Math.Max(1, maxHp);
        context.Character.CurrentHp = context.ExplicitCurrentHp ?? context.Character.MaxHp;
        stats.Level = level;
        stats.HpPerLevel ??= stats.Endurance;

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message =
                $"Derived MaxHp={context.Character.MaxHp} for {context.Character.Name} (Fallout L{level}, END {stats.Endurance} + LUCK {stats.Luck} + {level - 1}×{hpPerLevel}).",
        });
    }

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default)
    {
        if (context.Character.SystemStats is not Fallout2d20Extension stats)
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        var level = Math.Max(1, stats.Level ?? 1);
        var hpPerLevel = stats.HpPerLevel ?? stats.Endurance;
        var gain = context.LevelsGained * hpPerLevel;
        context.Character.MaxHp = Math.Max(0, context.Character.MaxHp) + gain;
        stats.Level = level + context.LevelsGained;

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Level gain: +{gain} HP for {context.Character.Name} (now {context.Character.MaxHp} MaxHp, L{stats.Level}).",
        });
    }

    private static int? ParseLevel(string? classLevel)
    {
        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return null;
        }

        var parts = classLevel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && int.TryParse(parts[^1], out var level) ? Math.Max(1, level) : null;
    }
}