using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Pf2eDeriveHitPointsStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "pf2e.derive_hit_points";

    public bool CanApply(BootstrapContext context) =>
        !context.HasExplicitMaxHp
        && context.Character.SystemStats is Pf2eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;
        if (!TryGetInputs(stats, context, out var level, out var classHp, out var ancestryHp, out var conMod))
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        var maxHp = ancestryHp + level * (classHp + conMod);
        context.Character.MaxHp = Math.Max(1, maxHp);
        context.Character.CurrentHp = context.ExplicitCurrentHp ?? context.Character.MaxHp;
        stats.Level ??= level;

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message =
                $"Derived MaxHp={context.Character.MaxHp} for {context.Character.Name} (PF2e L{level}, ancestry {ancestryHp}, class {classHp}/level, CON mod {conMod:+#;-#;+0}).",
        });
    }

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default)
    {
        if (context.Character.SystemStats is not Pf2eExtension stats)
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        if (!TryGetInputs(stats, context, out var level, out var classHp, out _, out var conMod))
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        var gain = context.LevelsGained * (classHp + conMod);
        context.Character.MaxHp = Math.Max(0, context.Character.MaxHp) + gain;
        stats.Level = level + context.LevelsGained;

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Level gain: +{gain} HP for {context.Character.Name} (now {context.Character.MaxHp} MaxHp, L{stats.Level}).",
        });
    }

    private static bool TryGetInputs(
        Pf2eExtension stats,
        BootstrapContext context,
        out int level,
        out int classHp,
        out int ancestryHp,
        out int conMod)
    {
        level = stats.Level ?? ParseLevelFromClassLevel(context.Character.ClassLevel) ?? 1;
        classHp = stats.ClassHpPerLevel ?? 0;
        ancestryHp = stats.AncestryHp ?? 0;
        conMod = stats.ConstitutionMod;

        return classHp > 0 && ancestryHp > 0 && level >= 1;
    }

    private static int? ParseLevelFromClassLevel(string? classLevel)
    {
        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return null;
        }

        var parts = classLevel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[^1], out var level))
        {
            return Math.Max(1, level);
        }

        return null;
    }
}