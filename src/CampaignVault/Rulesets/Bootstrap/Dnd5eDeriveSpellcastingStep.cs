using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveSpellcastingStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_spellcasting";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    private static BootstrapStepResult? ApplySpellcasting(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        var classLevels = Dnd5eClassProfileResolver.ParseClassLevels(
            context.Character.ClassLevel,
            stats.ClassLevels);

        if (!Dnd5eClassProfileResolver.TryResolve(
                context.Character.ClassLevel,
                stats.HitDie,
                stats.Level,
                stats.ClassLevels,
                out var level,
                out _))
        {
            if (stats.Level is null or < 1)
            {
                return null;
            }

            level = stats.Level.Value;
        }

        var ability = stats.SpellcastingAbility
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(classLevels.Count > 0 ? classLevels : stats.ClassLevels)
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(context.Character.ClassLevel);

        if (string.IsNullOrWhiteSpace(ability))
        {
            return null;
        }

        stats.SpellcastingAbility ??= ability;
        var proficiency = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        var saveDc = Dnd5eSpellcastingHelper.ComputeSpellSaveDc(stats, proficiency, stats.SpellcastingAbility);
        var attackBonus = Dnd5eSpellcastingHelper.ComputeSpellAttackBonus(stats, proficiency, stats.SpellcastingAbility);

        var changed = false;
        if (!stats.SpellSaveDc.HasValue)
        {
            stats.SpellSaveDc = saveDc;
            changed = true;
        }

        if (!stats.SpellAttackBonus.HasValue)
        {
            stats.SpellAttackBonus = attackBonus;
            changed = true;
        }

        if (!changed && stats.SpellcastingAbility == ability)
        {
            return null;
        }

        return new BootstrapStepResult
        {
            StepName = "dnd5e.derive_spellcasting",
            Message =
                $"Set spellcasting ({stats.SpellcastingAbility}) on {context.Character.Name}: spellSaveDc={stats.SpellSaveDc}, spellAttackBonus={stats.SpellAttackBonus}.",
        };
    }
}