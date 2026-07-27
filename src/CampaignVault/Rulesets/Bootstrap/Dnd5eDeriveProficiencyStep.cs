using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveProficiencyStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_proficiency";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    private static BootstrapStepResult? ApplyProficiency(BootstrapContext context)
    {
        var stats = (Dnd5eExtension)context.Character.SystemStats;
        stats.Attributes ??= [];

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

        if (level < 1)
        {
            return null;
        }

        var prof = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        if (stats.Attributes.TryGetValue("proficiencyBonus", out var existing) && Math.Abs(existing - prof) < 0.01f)
        {
            return null;
        }

        stats.Attributes["proficiencyBonus"] = prof;
        stats.Level ??= level;

        return new BootstrapStepResult
        {
            StepName = "dnd5e.derive_proficiency",
            Message = $"Set proficiencyBonus={prof} (level {level}) on {context.Character.Name}.",
        };
    }
}