using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

public sealed class Dnd5eDeriveProficiencyStep(
    ClassDefinitionProvider? classProvider = null,
    BackgroundDefinitionProvider? backgroundProvider = null) : IBootstrapStep, ILevelGainStep
{
    public string Name => "dnd5e.derive_proficiency";

    public bool CanApply(BootstrapContext context) =>
        context.Character.SystemStats is Dnd5eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    public Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default) =>
        Task.FromResult(ApplyProficiency(context));

    private BootstrapStepResult? ApplyProficiency(BootstrapContext context)
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
        var isFirstDerivation = !stats.Attributes.ContainsKey("proficiencyBonus");
        var profChanged = !stats.Attributes.TryGetValue("proficiencyBonus", out var existing) || Math.Abs(existing - prof) >= 0.01f;

        var derivedSkills = DeriveBackgroundSkillModifiers(context, stats, prof);
        var derivedSaves = DeriveClassSavingThrowModifiers(context, stats, prof);
        var hints = isFirstDerivation ? BuildClassSkillChoiceHints(context, stats) : [];

        if (!profChanged && derivedSkills.Count == 0 && derivedSaves.Count == 0 && hints.Count == 0)
        {
            return null;
        }

        stats.Attributes["proficiencyBonus"] = prof;
        stats.Level ??= level;

        var messageParts = new List<string> { $"Set proficiencyBonus={prof} (level {level})" };
        if (derivedSkills.Count > 0)
        {
            messageParts.Add($"skillModifiers[{string.Join(", ", derivedSkills)}]");
        }

        if (derivedSaves.Count > 0)
        {
            messageParts.Add($"savingThrowModifiers[{string.Join(", ", derivedSaves)}]");
        }

        return new BootstrapStepResult
        {
            StepName = "dnd5e.derive_proficiency",
            Message = $"{string.Join(", ", messageParts)} on {context.Character.Name}.",
            LlmHints = hints,
        };
    }

    /// <summary>
    /// Class-granted skill proficiencies (e.g. Fighter chooses 2 from a class-specific list) are a player
    /// choice with no fixed formula — we don't encode PHB skill-choice lists in our own data (the calling
    /// LLM already knows them and would just be duplicating/maintaining a second copy). Instead, nudge once
    /// at first bootstrap: if the character has a resolvable class and background-granted skills are the
    /// only ones present, remind the LLM to pick and commit the class's skill proficiencies itself via a
    /// system_stats change, mirroring the existing armor-equip hint in Dnd5eDeriveDefenseStep.
    /// </summary>
    private List<string> BuildClassSkillChoiceHints(BootstrapContext context, Dnd5eExtension stats)
    {
        var classLevels = Dnd5eClassProfileResolver.ParseClassLevels(context.Character.ClassLevel, stats.ClassLevels);
        if (classLevels.Count == 0)
        {
            return [];
        }

        var classNames = string.Join("/", classLevels.Select(e => e.Class));
        return
        [
            $"{context.Character.Name} ({classNames}) — remember to pick and commit class-granted skill proficiencies "
            + "(per the class's PHB skill list, e.g. Fighter chooses 2, Rogue chooses 4) via a system_stats change: "
            + "systemStats.skillModifiers[skillName] = ability modifier + proficiencyBonus. "
            + "Background-granted skills are already derived automatically; class choices are not, since they're a player pick.",
        ];
    }

    /// <summary>
    /// Fills SkillModifiers for skills granted by the character's background, using ability mod + proficiency bonus.
    /// Never overwrites a skill the caller already set (DM override, Expertise, etc.).
    /// Does not derive class-granted "choose N skills" proficiencies — no class data currently records those choices.
    /// </summary>
    private List<string> DeriveBackgroundSkillModifiers(BootstrapContext context, Dnd5eExtension stats, int prof)
    {
        var applied = new List<string>();
        if (backgroundProvider is null || string.IsNullOrWhiteSpace(stats.Background))
        {
            return applied;
        }

        if (!backgroundProvider.TryGet(RulesetSystem.Dnd5e, stats.Background, out var background) || background is null)
        {
            return applied;
        }

        foreach (var skill in background.SkillProficiencies)
        {
            if (stats.SkillModifiers.ContainsKey(skill))
            {
                continue;
            }

            if (!Dnd5eSkillTable.GoverningAbility.TryGetValue(skill, out var ability))
            {
                continue;
            }

            var abilityScore = GetAbilityScore(stats, ability);
            stats.SkillModifiers[skill] = stats.GetAbilityModifier(abilityScore) + prof;
            applied.Add(skill);
        }

        return applied;
    }

    /// <summary>
    /// Fills SavingThrowModifiers for saves the character's class(es) are proficient in, using ability mod + proficiency bonus.
    /// Never overwrites a save the caller already set.
    /// </summary>
    private List<string> DeriveClassSavingThrowModifiers(BootstrapContext context, Dnd5eExtension stats, int prof)
    {
        var applied = new List<string>();
        if (classProvider is null)
        {
            return applied;
        }

        var classLevels = Dnd5eClassProfileResolver.ParseClassLevels(context.Character.ClassLevel, stats.ClassLevels);
        foreach (var entry in classLevels)
        {
            if (!classProvider.TryResolveClass(RulesetSystem.Dnd5e, entry.Class, out var classDef) || classDef is null)
            {
                continue;
            }

            foreach (var ability in classDef.SavingThrows)
            {
                if (stats.SavingThrowModifiers.ContainsKey(ability))
                {
                    continue;
                }

                var abilityScore = GetAbilityScore(stats, ability);
                stats.SavingThrowModifiers[ability] = stats.GetAbilityModifier(abilityScore) + prof;
                applied.Add(ability);
            }
        }

        return applied;
    }

    private static int GetAbilityScore(Dnd5eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.Strength,
        "dexterity" => stats.Dexterity,
        "constitution" => stats.Constitution,
        "intelligence" => stats.Intelligence,
        "wisdom" => stats.Wisdom,
        "charisma" => stats.Charisma,
        _ => 10,
    };
}