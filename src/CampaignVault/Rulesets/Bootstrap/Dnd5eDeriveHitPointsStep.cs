using CampaignVault.Data;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveHitPointsStep(IRollService rollService) : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_hit_points";

    public bool CanApply(BootstrapContext context) =>
        !context.HasExplicitMaxHp
        && context.Character.SystemStats is Dnd5eExtension;

    public async Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        if (!TryGetInputs(stats, context, out var classLevels, out var level, out var dieSides, out var conMod, out var mode))
        {
            return null;
        }

        var (maxHp, detail) = classLevels.Count > 1
            ? await ComputeMulticlassMaxHpAsync(classLevels, conMod, mode, ct)
            : await ComputeMaxHpAsync(level, dieSides, conMod, mode, ct);

        context.Character.MaxHp = maxHp;
        context.Character.CurrentHp = context.ExplicitCurrentHp ?? maxHp;

        if (stats.Level is null or < 1)
        {
            stats.Level = level;
        }

        if (string.IsNullOrWhiteSpace(stats.HitDie))
        {
            stats.HitDie = $"d{dieSides}";
        }

        return new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Derived MaxHp={maxHp} for {context.Character.Name} (d{dieSides} L{level} CON{conMod:+#;-#;+0}, {mode}). {detail}",
        };
    }

    public async Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default)
    {
        if (context.Character.SystemStats is not Dnd5eExtension stats)
        {
            return null;
        }

        if (!TryGetInputs(stats, context, out var classLevels, out var level, out var dieSides, out var conMod, out var mode))
        {
            return null;
        }

        if (context.LevelsGained > 0
            && !string.IsNullOrWhiteSpace(context.ClassGained)
            && Dnd5eClassProfileResolver.TryResolveHitDie(context.ClassGained, out var gainedDie))
        {
            dieSides = gainedDie;
        }
        else if (classLevels.Count > 0)
        {
            dieSides = Dnd5eClassProfileResolver.TryResolveHitDie(classLevels[^1].Class, out var lastDie)
                ? lastDie
                : dieSides;
        }

        var gain = await ComputeLevelGainAsync(dieSides, conMod, mode, ct);
        var previousMax = context.Character.MaxHp;
        context.Character.MaxHp = Math.Max(0, previousMax) + gain;
        stats.Level = level + context.LevelsGained;

        return new BootstrapStepResult
        {
            StepName = Name,
            Message =
                $"Level gain: +{gain} HP for {context.Character.Name} (now {context.Character.MaxHp} MaxHp, L{stats.Level}).",
        };
    }

    private bool TryGetInputs(
        Dnd5eExtension stats,
        BootstrapContext context,
        out IReadOnlyList<ClassLevelEntry> classLevels,
        out int level,
        out int dieSides,
        out int conMod,
        out HitPointDerivationMode mode)
    {
        level = stats.Level ?? 1;
        conMod = stats.GetAbilityModifier(stats.Constitution);
        mode = context.HpModeOverride ?? stats.HpMode ?? HitPointDerivationMode.Average;
        classLevels = Dnd5eClassProfileResolver.ParseClassLevels(context.Character.ClassLevel, stats.ClassLevels);

        if (Dnd5eClassProfileResolver.TryResolve(
                context.Character.ClassLevel,
                stats.HitDie,
                stats.Level,
                classLevels,
                out level,
                out dieSides))
        {
            return level >= 1;
        }

        dieSides = 0;
        return false;
    }

    private async Task<(int MaxHp, string Detail)> ComputeMulticlassMaxHpAsync(
        IReadOnlyList<ClassLevelEntry> classLevels,
        int conMod,
        HitPointDerivationMode mode,
        CancellationToken ct)
    {
        var total = 0;
        var isFirstLevelEver = true;
        var details = new List<string>();

        foreach (var entry in classLevels)
        {
            if (!Dnd5eClassProfileResolver.TryResolveHitDie(entry.Class, out var dieSides))
            {
                continue;
            }

            for (var classLevel = 1; classLevel <= entry.Level; classLevel++)
            {
                if (isFirstLevelEver && classLevel == 1)
                {
                    total += dieSides + conMod;
                    details.Add($"{entry.Class} L1 max d{dieSides}");
                    isFirstLevelEver = false;
                }
                else
                {
                    var gain = await ComputeLevelGainAsync(dieSides, conMod, mode, ct);
                    total += gain;
                    details.Add($"{entry.Class} L{classLevel} +{gain}");
                }
            }
        }

        return (Math.Max(1, total), $"multiclass [{string.Join(", ", details)}]");
    }

    private async Task<(int MaxHp, string Detail)> ComputeMaxHpAsync(
        int level,
        int dieSides,
        int conMod,
        HitPointDerivationMode mode,
        CancellationToken ct)
    {
        var firstLevel = dieSides + conMod;
        if (level <= 1)
        {
            return (Math.Max(1, firstLevel), "level 1 only");
        }

        var additional = 0;
        var rollDetails = new List<string>();
        for (var i = 2; i <= level; i++)
        {
            var gain = await ComputeLevelGainAsync(dieSides, conMod, mode, ct);
            additional += gain;
            if (mode == HitPointDerivationMode.Rolled)
            {
                rollDetails.Add(gain.ToString());
            }
        }

        var total = Math.Max(1, firstLevel + additional);
        var detail = mode == HitPointDerivationMode.Rolled && rollDetails.Count > 0
            ? $"rolled levels 2-{level}: [{string.Join(", ", rollDetails)}]"
            : $"averaged levels 2-{level}";

        return (total, detail);
    }

    private async Task<int> ComputeLevelGainAsync(
        int dieSides,
        int conMod,
        HitPointDerivationMode mode,
        CancellationToken ct)
    {
        if (mode == HitPointDerivationMode.Rolled)
        {
            var outcome = await rollService.RollAsync(new RollRequest
            {
                Tag = "hp-level",
                Expression = $"1d{dieSides}",
                Bonus = conMod,
            }, ct);
            return Math.Max(1, outcome.Result);
        }

        return Math.Max(1, Dnd5eClassProfileResolver.AverageDieRoll(dieSides) + conMod);
    }
}