using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Pf2eDeriveAncestryStep(RaceDefinitionProvider raceProvider) : IBootstrapStep
{
    public string Name => "pf2e.derive_ancestry";

    public bool CanApply(BootstrapContext context) =>
        context.Trigger is BootstrapTrigger.Create or BootstrapTrigger.Upsert
        && context.Character.SystemStats is Pf2eExtension { Ancestry.Length: > 0 };

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;
        if (!raceProvider.TryGet(RulesetSystem.Pathfinder2e, stats.Ancestry!, out var ancestry) || ancestry is null)
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        // Speed, size and traits are idempotent, so world_build upserts get them too; ability bonuses are
        // additive and only land once, when the character is created.
        ApplyAncestryTraits(context.Character, stats, ancestry, context.Trigger == BootstrapTrigger.Create);

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Applied ancestry '{stats.Ancestry}' traits to {context.Character.Name}.",
        });
    }

    internal static void ApplyAncestryTraits(Character character, Pf2eExtension stats, RaceDefinition ancestry, bool applyAbilityBonuses = true)
    {
        // PF2e tracks ability modifiers directly (no raw ability score), so ancestry ability
        // boosts are applied as direct modifier deltas rather than raw-score bonuses.
        foreach (var (ability, bonus) in applyAbilityBonuses ? ancestry.AbilityBonuses : [])
        {
            switch (ability.ToLowerInvariant())
            {
                case "strength": stats.StrengthMod += bonus; break;
                case "dexterity": stats.DexterityMod += bonus; break;
                case "constitution": stats.ConstitutionMod += bonus; break;
                case "intelligence": stats.IntelligenceMod += bonus; break;
                case "wisdom": stats.WisdomMod += bonus; break;
                case "charisma": stats.CharismaMod += bonus; break;
            }
        }

        if (ancestry.BaseSpeed is { } speed && stats.Movement is null)
        {
            stats.Movement = speed;
        }

        RaceTraitStamper.StampSizeAndTraits(character, ancestry.Size, ancestry.Traits);
    }
}
