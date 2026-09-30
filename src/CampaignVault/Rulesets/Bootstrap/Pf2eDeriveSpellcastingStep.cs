using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Spell DC for PF2e casters. The casting ability comes from the stats when set explicitly (a feat or
/// archetype, or the model), else from the class YAML's <c>spellcastingAbility</c>; a character with
/// neither is not a caster and gets no spell DC.
/// </summary>
public sealed class Pf2eDeriveSpellcastingStep(ClassDefinitionProvider? classProvider = null) : IBootstrapStep, ILevelGainStep
{
    public string Name => "pf2e.derive_spellcasting";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Pf2eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplySpellcasting(context));

    private BootstrapStepResult? ApplySpellcasting(BootstrapContext context)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;

        if (stats.Level is null or < 1)
        {
            return null;
        }

        var level = stats.Level.Value;

        var ability = stats.SpellcastingAbility ?? ClassSpellcastingAbility(context.Character);
        if (string.IsNullOrWhiteSpace(ability))
        {
            return null;
        }

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

    private string? ClassSpellcastingAbility(Character character)
    {
        var classDefs = (classProvider ?? ClassAliasMatcher.DefaultProvider)
            .GetClassesForSystem(RulesetSystem.Pathfinder2e);

        return CharacterClassResolver.ResolveClassLevels(character)
            .Select(entry => ClassAliasMatcher.Resolve(entry.Class, classDefs)?.SpellcastingAbility)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }
}
