using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveRaceStep(RaceDefinitionProvider raceProvider) : IBootstrapStep
{
    public string Name => "dnd5e.derive_race";

    public bool CanApply(BootstrapContext context) =>
        context.Trigger is BootstrapTrigger.Create or BootstrapTrigger.Upsert
        && context.Character.SystemStats is Dnd5eExtension { Race.Length: > 0 };

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        if (!raceProvider.TryGet(RulesetSystem.Dnd5e, stats.Race!, out var race) || race is null)
        {
            return Task.FromResult<BootstrapStepResult?>(null);
        }

        // Speed, size and traits are idempotent, so world_build upserts get them too; ability bonuses are
        // additive and only land once, when the character is created.
        ApplyRaceTraits(context.Character, stats, race, context.Trigger == BootstrapTrigger.Create);

        return Task.FromResult<BootstrapStepResult?>(new BootstrapStepResult
        {
            StepName = Name,
            Message = $"Applied race '{stats.Race}' traits to {context.Character.Name}.",
        });
    }

    internal static void ApplyRaceTraits(Character character, Dnd5eExtension stats, RaceDefinition race, bool applyAbilityBonuses = true)
    {
        foreach (var (ability, bonus) in applyAbilityBonuses ? race.AbilityBonuses : [])
        {
            switch (ability.ToLowerInvariant())
            {
                case "strength": stats.Strength += bonus; break;
                case "dexterity": stats.Dexterity += bonus; break;
                case "constitution": stats.Constitution += bonus; break;
                case "intelligence": stats.Intelligence += bonus; break;
                case "wisdom": stats.Wisdom += bonus; break;
                case "charisma": stats.Charisma += bonus; break;
            }
        }

        if (race.BaseSpeed is { } speed && stats.Movement is null)
        {
            stats.Movement = speed;
        }

        RaceTraitStamper.StampSizeAndTraits(character, race.Size, race.Traits);
    }
}
