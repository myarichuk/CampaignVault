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
        derivedSkills.AddRange(DeriveChosenSkillModifiers(stats, prof));
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
    /// The class's level-1 skill picks are recorded as <c>levelUpChoices</c> with key <c>skills</c> (the character
    /// builder writes them; the class YAML's <c>skillChoices</c> says how many from which list). Without any, remind the
    /// caller once, at first derivation, to record them the same way rather than leave the class skills out.
    /// </summary>
    private static List<string> BuildClassSkillChoiceHints(BootstrapContext context, Dnd5eExtension stats)
    {
        var classLevels = Dnd5eClassProfileResolver.ParseClassLevels(context.Character.ClassLevel, stats.ClassLevels);
        if (classLevels.Count == 0 || stats.LevelUpChoices.Any(IsSkillChoice))
        {
            return [];
        }

        var classNames = string.Join("/", classLevels.Select(e => e.Class));
        return
        [
            $"{context.Character.Name} ({classNames}) has no class skill picks. Record them as systemStats.levelUpChoices "
            + "[{ level: 1, key: \"skills\", value: \"<Skill>\" }, ...] (the class's count and list: lookup kind=handbook), "
            + "and the engine derives their modifiers. Background skills are already derived.",
        ];
    }

    /// <summary>
    /// Fills SkillModifiers for the class skills recorded as <c>skills</c> choices, using ability mod + proficiency bonus.
    /// Never overwrites a skill the caller already set (DM override, Expertise, background).
    /// </summary>
    private static List<string> DeriveChosenSkillModifiers(Dnd5eExtension stats, int prof)
    {
        var applied = new List<string>();
        foreach (var choice in stats.LevelUpChoices.Where(IsSkillChoice))
        {
            if (!Dnd5eSkillTable.GoverningAbility.TryGetValue(choice.Value, out var ability)
                || stats.SkillModifiers.Keys.Any(k => k.Equals(choice.Value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The table's spelling, so "sleight of hand" and "Sleight of Hand" are one skill.
            var skill = Dnd5eSkillTable.GoverningAbility.Keys.First(k => k.Equals(choice.Value, StringComparison.OrdinalIgnoreCase));
            stats.SkillModifiers[skill] = stats.GetAbilityModifier(GetAbilityScore(stats, ability)) + prof;
            applied.Add(skill);
        }

        return applied;
    }

    private static bool IsSkillChoice(LevelUpChoiceRecord choice) =>
        choice.Key.Equals(SkillsChoiceKey, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(choice.Value);

    /// <summary>The <c>levelUpChoices</c> key class skill picks are recorded under.</summary>
    public const string SkillsChoiceKey = "skills";

    /// <summary>
    /// Fills SkillModifiers for skills granted by the character's background, using ability mod + proficiency bonus.
    /// Never overwrites a skill the caller already set (DM override, Expertise, etc.).
    /// Class skill picks are <see cref="DeriveChosenSkillModifiers"/>.
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
    /// Fills SavingThrowModifiers for saves the character's starting class is proficient in, using ability mod + proficiency bonus.
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
        // Saving-throw proficiencies come from the starting class only; multiclassing grants none.
        foreach (var entry in classLevels.Take(1))
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