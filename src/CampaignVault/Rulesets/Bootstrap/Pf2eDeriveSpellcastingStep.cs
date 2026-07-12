using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Pf2eDeriveSpellcastingStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "pf2e.derive_spellcasting";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Pf2eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    private static BootstrapStepResult? ApplySpellcasting(BootstrapContext context)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;

        if (stats.Level is null or < 1)
        {
            return null;
        }

        var level = stats.Level.Value;

        var ability = stats.SpellcastingAbility
            ?? InferSpellcastingAbility(context.Character.ClassLevel)
            ?? "Wisdom";

        stats.SpellcastingAbility ??= ability;

        var abilityMod = ability.ToLower() switch
        {
            "strength" => stats.StrengthMod,
            "dexterity" => stats.DexterityMod,
            "constitution" => stats.ConstitutionMod,
            "intelligence" => stats.IntelligenceMod,
            "wisdom" => stats.WisdomMod,
            "charisma" => stats.CharismaMod,
            _ => 0
        };

        var proficiencyRank = stats.SpellcastingProficiency ?? Pf2eProficiencyRank.Trained;

        var proficiencyBonus = proficiencyRank == Pf2eProficiencyRank.Untrained
            ? 0
            : level + (int)proficiencyRank;

        var spellDc = 10 + abilityMod + proficiencyBonus;

        var changed = false;
        if (!stats.SpellDc.HasValue)
        {
            stats.SpellDc = spellDc;
            changed = true;
        }

        if (!changed && stats.SpellcastingAbility == ability)
        {
            return null;
        }

        return new BootstrapStepResult
        {
            StepName = "pf2e.derive_spellcasting",
            Message = $"Set spellcasting ({stats.SpellcastingAbility}) on {context.Character.Name}: spellDc={stats.SpellDc} (level {level}, {proficiencyRank} proficiency).",
        };
    }

    private static string? InferSpellcastingAbility(string? classLevel)
    {
        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return null;
        }

        var lower = classLevel.ToLower();
        return lower switch
        {
            var s when s.Contains("wizard") || s.Contains("alchemist") => "Intelligence",
            var s when s.Contains("cleric") || s.Contains("druid") || s.Contains("ranger") || s.Contains("monk") => "Wisdom",
            var s when s.Contains("bard") || s.Contains("sorcerer") || s.Contains("champion") => "Charisma",
            _ => null
        };
    }
}
