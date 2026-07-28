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

        // Initialize spellcasting (class DC) proficiency from standard PF2e progression if not set.
        // Approximation: Trained at 1, Expert at 7, Master at 15 — no Legendary tier (class-specific,
        // rare, and not derivable from level alone). Unused for non-casters (no SpellDc is consulted
        // for them), so this is harmless to set unconditionally like AC/skill/save proficiency above.
        if (stats.SpellcastingProficiency is null)
        {
            stats.SpellcastingProficiency = level >= 15 ? Pf2eProficiencyRank.Master
                : level >= 7 ? Pf2eProficiencyRank.Expert
                : Pf2eProficiencyRank.Trained;
            changed = true;
        }

        var derivedSkills = DeriveNumericModifiers(stats, level, stats.SkillProficiencies, stats.SkillModifiers, Pf2eSkillTable.KeyAbility);
        var derivedSaves = DeriveNumericModifiers(stats, level, stats.SaveProficiencies, stats.SavingThrowModifiers, Pf2eSkillTable.SaveKeyAbility);
        if (derivedSkills.Count > 0 || derivedSaves.Count > 0)
        {
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

    /// <summary>
    /// Converts proficiency ranks into numeric modifiers (ability mod + proficiency bonus, where proficiency
    /// bonus is 0 if Untrained, else level + rank value per PF2e rules). Never overwrites an entry already
    /// present in <paramref name="modifiers"/> (DM override or previously-derived value).
    /// </summary>
    private static List<string> DeriveNumericModifiers(
        Pf2eExtension stats,
        int level,
        IReadOnlyDictionary<string, Pf2eProficiencyRank> ranks,
        Dictionary<string, int> modifiers,
        IReadOnlyDictionary<string, string> keyAbilities)
    {
        var applied = new List<string>();
        foreach (var (name, rank) in ranks)
        {
            if (modifiers.ContainsKey(name) || !keyAbilities.TryGetValue(name, out var ability))
            {
                continue;
            }

            var proficiencyBonus = rank == Pf2eProficiencyRank.Untrained ? 0 : level + (int)rank;
            modifiers[name] = GetAbilityModifier(stats, ability) + proficiencyBonus;
            applied.Add(name);
        }

        return applied;
    }

    private static int GetAbilityModifier(Pf2eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.StrengthMod,
        "dexterity" => stats.DexterityMod,
        "constitution" => stats.ConstitutionMod,
        "intelligence" => stats.IntelligenceMod,
        "wisdom" => stats.WisdomMod,
        "charisma" => stats.CharismaMod,
        _ => 0,
    };
}
