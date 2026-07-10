using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Pf2eDeriveProficiencyStep : IBootstrapStep, ILevelGainStep
{
    public string Name => "pf2e.derive_proficiency";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Pf2eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    private static BootstrapStepResult? ApplyProficiency(BootstrapContext context)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;

        if (stats.Level is null or < 1)
        {
            return null;
        }

        var level = stats.Level.Value;
        var changed = false;

        // Initialize AC proficiency to Trained (standard default) if not set
        if (stats.AcProficiency == Pf2eProficiencyRank.Untrained)
        {
            stats.AcProficiency = Pf2eProficiencyRank.Trained;
            changed = true;
        }

        // Initialize skill proficiencies to Trained if empty
        if (stats.SkillProficiencies.Count == 0)
        {
            var defaultSkills = new[]
            {
                "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy",
                "Intimidation", "Medicine", "Nature", "Occultism", "Performance", "Religion",
                "Society", "Stealth", "Survival", "Thievery", "Lore"
            };

            foreach (var skill in defaultSkills)
            {
                if (!stats.SkillProficiencies.ContainsKey(skill))
                {
                    stats.SkillProficiencies[skill] = Pf2eProficiencyRank.Trained;
                }
            }
            changed = true;
        }

        // Initialize save proficiencies to Trained if empty
        if (stats.SaveProficiencies.Count == 0)
        {
            var saves = new[] { "Fortitude", "Reflex", "Will" };
            foreach (var save in saves)
            {
                stats.SaveProficiencies[save] = Pf2eProficiencyRank.Trained;
            }
            changed = true;
        }

        if (!changed)
        {
            return null;
        }

        return new BootstrapStepResult
        {
            StepName = "pf2e.derive_proficiency",
            Message = $"Set proficiency ranks on {context.Character.Name} (level {level}): AC={stats.AcProficiency}, Skills/Saves initialized to Trained.",
        };
    }
}
