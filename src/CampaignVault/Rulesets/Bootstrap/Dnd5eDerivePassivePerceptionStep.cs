using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDerivePassivePerceptionStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_passive_perception";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyPassivePerception(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyPassivePerception(context));

    private static BootstrapStepResult? ApplyPassivePerception(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        stats.Attributes ??= [];

        var perceptionMod = stats.SkillModifiers
            .FirstOrDefault(kv => string.Equals(kv.Key, "Perception", StringComparison.OrdinalIgnoreCase)).Value;
        if (perceptionMod == 0)
        {
            perceptionMod = stats.GetAbilityModifier(stats.Wisdom);
        }

        if (perceptionMod == 0 && stats.Wisdom == 10
            && !stats.SkillModifiers.Keys.Any(k => string.Equals(k, "Perception", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var passive = 10 + perceptionMod;
        if (stats.Attributes.TryGetValue("passivePerception", out var existing)
            && Math.Abs(existing - passive) < 0.01f)
        {
            return null;
        }

        stats.Attributes["passivePerception"] = passive;

        return new BootstrapStepResult
        {
            StepName = "dnd5e.derive_passive_perception",
            Message = $"Set passivePerception={passive} on {context.Character.Name}.",
        };
    }
}