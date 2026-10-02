using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// What a PF2e character's ancestry, class and background give, from their templates:
/// <list type="bullet">
/// <item>the HP inputs: <c>ancestryHp</c> and <c>classHpPerLevel</c> (the class's hit die), when not set;</item>
/// <item>on creation, for a character built with level-1 skill picks (the character builder records them as
/// <c>levelUpChoices</c> "skills", and the background's choice of two as "backgroundSkill"): its trained skills, which
/// are the class's fixed ones, the background's skill and Lore, those picks and the skills of the class feature options
/// it recorded (a racket's), then its recorded skill increases (the progression's SkillIncrease choices) in level order;</item>
/// <item>on creation, the background's skill feat.</item>
/// </list>
/// Runs before the HP and proficiency steps, so they derive from it (and the proficiency step's "every skill trained"
/// default doesn't apply to a built character).
/// </summary>
public sealed class Pf2eDeriveOriginStep(
    RaceDefinitionProvider? raceProvider,
    ClassDefinitionProvider? classProvider,
    BackgroundDefinitionProvider? backgroundProvider,
    ProgressionDefinitionProvider? progressionProvider = null) : IBootstrapStep
{
    /// <summary>The <c>levelUpChoices</c> key of the skill picked from a background's two (<see cref="BackgroundDefinition.SkillOptions"/>).</summary>
    public const string BackgroundSkillChoiceKey = "backgroundSkill";

    public string Name => "pf2e.derive_origin";

    public bool CanApply(BootstrapContext context) =>
        context.Trigger is BootstrapTrigger.Create or BootstrapTrigger.Upsert
        && context.Character.SystemStats is Pf2eExtension;

    public Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var stats = (Pf2eExtension)context.Character.SystemStats;
        var applied = new List<string>();

        var ancestry = stats.Ancestry is { Length: > 0 } a && raceProvider?.TryGet(RulesetSystem.Pathfinder2e, a, out var race) == true ? race : null;
        if (stats.AncestryHp is null && ancestry?.Hp is { } ancestryHp)
        {
            stats.AncestryHp = ancestryHp;
            applied.Add($"ancestry HP {ancestryHp}");
        }

        var cls = Class(context.Character);
        if (stats.ClassHpPerLevel is null && HitDie(cls?.HitDie) is { } classHp)
        {
            stats.ClassHpPerLevel = classHp;
            applied.Add($"class HP {classHp}/level");
        }

        var background = stats.Background is { Length: > 0 } b && backgroundProvider?.TryGet(RulesetSystem.Pathfinder2e, b, out var bg) == true ? bg : null;
        if (context.Trigger == BootstrapTrigger.Create)
        {
            var progression = cls is not null && progressionProvider?.TryGetProgression(RulesetSystem.Pathfinder2e, cls.Name, out var p) == true ? p : null;
            var trained = TrainedSkills(stats, cls, background, progression);
            if (trained.Count > 0)
            {
                foreach (var skill in trained)
                    stats.SkillProficiencies[skill] = Pf2eProficiencyRank.Trained;
                applied.Add($"trained in {string.Join(", ", trained)}");

                var increased = ApplySkillIncreases(stats, progression);
                if (increased.Count > 0)
                    applied.Add($"skill increases {string.Join(", ", increased)}");
            }

            if (background?.SkillFeat is { Length: > 0 } feat && !stats.SkillFeats.Contains(feat, StringComparer.OrdinalIgnoreCase))
            {
                stats.SkillFeats.Add(feat);
                applied.Add($"skill feat {feat} (background)");
            }
        }

        return Task.FromResult(applied.Count == 0
            ? null
            : new BootstrapStepResult
            {
                StepName = Name,
                Message = $"{context.Character.Name}'s ancestry, class and background: {string.Join("; ", applied)}.",
            });
    }

    /// <summary>
    /// The trained skills of a character built with level-1 skill picks, or none (a character made another way keeps the
    /// proficiency step's defaults). The Lore counts only when it names one ("Scribing Lore"), not a choice described in words.
    /// </summary>
    private static List<string> TrainedSkills(Pf2eExtension stats, ClassDefinition? cls, BackgroundDefinition? background, ProgressionDefinition? progression)
    {
        if (stats.SkillProficiencies.Count > 0
            || !stats.LevelUpChoices.Any(c => c.Level == 1 && c.Key.Equals(Dnd5eDeriveProficiencyStep.SkillsChoiceKey, StringComparison.OrdinalIgnoreCase)))
            return [];

        var picks = stats.LevelUpChoices
            .Where(c => c.Level == 1 && (c.Key.Equals(Dnd5eDeriveProficiencyStep.SkillsChoiceKey, StringComparison.OrdinalIgnoreCase)
                                         || c.Key.Equals(BackgroundSkillChoiceKey, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Value);
        var lore = background?.Lore is { } l && IsLoreName(l) ? [l] : Array.Empty<string>();
        return
        [
            .. (cls?.SkillChoices?.Trained ?? [])
                .Concat(background?.SkillProficiencies ?? [])
                .Concat(lore)
                .Concat(picks)
                .Concat(GrantedSkills(stats, progression))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The skills the recorded class feature picks train (a thief's Thievery), from the progression's options.</summary>
    private static IEnumerable<string> GrantedSkills(Pf2eExtension stats, ProgressionDefinition? progression)
    {
        if (progression is null)
            yield break;

        foreach (var (level, def) in progression.Levels)
        {
            foreach (var choice in def.Choices)
            {
                var record = stats.LevelUpChoices.FirstOrDefault(c => c.Level == level && c.Key.Equals(choice.Key, StringComparison.OrdinalIgnoreCase));
                var option = record is null ? null : progression.OptionsFor(choice).FirstOrDefault(o => o.Id.Equals(record.Value, StringComparison.OrdinalIgnoreCase));
                foreach (var skill in option?.Skills ?? [])
                    yield return skill;
            }
        }
    }

    /// <summary>Each recorded skill increase, in level order, a rank up (one the rules don't allow is skipped). "Athletics expert".</summary>
    private static List<string> ApplySkillIncreases(Pf2eExtension stats, ProgressionDefinition? progression)
    {
        var keys = progression?.Levels.Values.SelectMany(l => l.Choices).Where(c => c.Type == ChoiceType.SkillIncrease)
            .Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var applied = new List<string>();
        foreach (var record in stats.LevelUpChoices.Where(c => keys.Contains(c.Key)).OrderBy(c => c.Level))
        {
            var skill = stats.SkillProficiencies.Keys.FirstOrDefault(k => k.Equals(record.Value, StringComparison.OrdinalIgnoreCase)) ?? record.Value;
            if (Pf2eSkillRanks.Raise(stats.SkillProficiencies, skill, record.Level) is null)
                applied.Add($"{skill} {stats.SkillProficiencies[skill].ToString().ToLowerInvariant()}");
        }

        return applied;
    }

    /// <summary>"Scribing Lore", not "Lore related to the terrain you lived in".</summary>
    internal static bool IsLoreName(string lore) =>
        lore.EndsWith(" Lore", StringComparison.Ordinal) && lore.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3;

    private ClassDefinition? Class(Character character)
    {
        if (classProvider is null)
            return null;

        foreach (var entry in CharacterClassResolver.ResolveClassLevels(character))
        {
            if (classProvider.TryResolveClass(RulesetSystem.Pathfinder2e, entry.Class, out var cls))
                return cls;
        }

        return null;
    }

    /// <summary>"d10" → 10.</summary>
    private static int? HitDie(string? hitDie) =>
        hitDie is { Length: > 1 } && (hitDie[0] is 'd' or 'D') && int.TryParse(hitDie[1..], out var n) && n > 0 ? n : null;
}
