using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Passive Perception (10 + the Perception modifier), plus what the character's effects add (<c>passiveBonus</c>, from its
/// class features, race and feats when the providers are given). An effect for Investigation adds a passive Investigation.
/// </summary>
public sealed class Dnd5eDerivePassivePerceptionStep(
    FeatDefinitionProvider? feats = null,
    ProgressionDefinitionProvider? progressions = null,
    RaceDefinitionProvider? races = null) : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_passive_perception";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyPassivePerception(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyPassivePerception(context));

    private BootstrapStepResult? ApplyPassivePerception(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        stats.Attributes ??= [];
        var effects = feats is null && progressions is null && races is null
            ? []
            : FeatEffectRules.DataEffects(context.Character, RulesetSystem.Dnd5e, feats, progressions, races);
        var perceptionBonus = FeatEffectRules.Sum(effects, FeatEffectKinds.PassiveBonus, "Perception").Total;
        var investigationBonus = FeatEffectRules.Sum(effects, FeatEffectKinds.PassiveBonus, "Investigation").Total;

        var messages = new List<string>();
        if (investigationBonus != 0)
        {
            var investigation = 10 + Modifier(stats, "Investigation", stats.Intelligence) + investigationBonus;
            if (!stats.Attributes.TryGetValue("passiveInvestigation", out var had) || Math.Abs(had - investigation) >= 0.01f)
            {
                stats.Attributes["passiveInvestigation"] = investigation;
                messages.Add($"passiveInvestigation={investigation}");
            }
        }

        var perceptionMod = Modifier(stats, "Perception", stats.Wisdom);
        var hasPerception = stats.SkillModifiers.Keys.Any(k => string.Equals(k, "Perception", StringComparison.OrdinalIgnoreCase));
        if (perceptionMod != 0 || stats.Wisdom != 10 || hasPerception || perceptionBonus != 0)
        {
            var passive = 10 + perceptionMod + perceptionBonus;
            if (!stats.Attributes.TryGetValue("passivePerception", out var existing) || Math.Abs(existing - passive) >= 0.01f)
            {
                stats.Attributes["passivePerception"] = passive;
                messages.Insert(0, $"passivePerception={passive}");
            }
        }

        return messages.Count == 0
            ? null
            : new BootstrapStepResult
            {
                StepName = "dnd5e.derive_passive_perception",
                Message = $"Set {string.Join(", ", messages)} on {context.Character.Name}.",
            };
    }

    /// <summary>The skill's modifier on the sheet, else its ability's (a skill the character isn't proficient in).</summary>
    private static int Modifier(Dnd5eExtension stats, string skill, int abilityScore)
    {
        var mod = stats.SkillModifiers.FirstOrDefault(kv => string.Equals(kv.Key, skill, StringComparison.OrdinalIgnoreCase)).Value;
        return mod != 0 ? mod : stats.GetAbilityModifier(abilityScore);
    }
}
