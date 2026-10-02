using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// PF2e's creation rules that data can't say. They read the PF2e recipe's steps by key: <c>ancestry</c>,
/// <c>background</c>, <c>class</c> (RulesetData/pf2e/creation/pc.yaml).
/// </summary>
public static class Pf2eRecipeValidatorNames
{
    public const string Boosts = "pf2e.boosts";
    public const string FeatEligibility = "pf2e.featEligibility";
    public const string ClassSkills = "pf2e.classSkills";
}

/// <summary>
/// Attribute boosts, one step per source, so "no two boosts to the same attribute from one source" is the pick list's
/// no-duplicates rule. On top of that: an ancestry's free boosts can't go where the ancestry already boosts, and one of
/// a background's two must go to one of the attributes it names.
/// </summary>
public sealed class Pf2eBoostsValidator : IRecipeValidator
{
    public string Name => Pf2eRecipeValidatorNames.Boosts;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var picks = draft.GetList(step.Key);
        if (string.Equals(step.Source, CreationSources.AncestryBoosts, StringComparison.OrdinalIgnoreCase)
            && ctx.Resolve("ancestry") is RaceDefinition ancestry)
        {
            var boosted = ancestry.AbilityBonuses.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            foreach (var pick in picks.Where(p => boosted.Contains(p, StringComparer.OrdinalIgnoreCase)))
                yield return CreationIssue.Error(step.Key, $"{CreationSources.Label(ancestry.Name)} already boosts {pick}; a free boost goes to another attribute.");
        }

        if (string.Equals(step.Source, CreationSources.BackgroundBoosts, StringComparison.OrdinalIgnoreCase)
            && ctx.Resolve("background") is BackgroundDefinition { Boosts.Count: > 0 } background
            && picks.Count > 0
            && !picks.Any(p => background.Boosts.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            yield return CreationIssue.Error(step.Key,
                $"One of {CreationSources.Label(background.Name)}'s boosts goes to {string.Join(" or ", background.Boosts)}.");
        }
    }
}

/// <summary>
/// Each feat picked must be one the character can take at its level from this step's category: a class feat of its
/// class, an ancestry feat of its ancestry. Its prerequisites are the core feat.prerequisites check's (the feats kind's).
/// </summary>
public sealed class Pf2eFeatEligibilityValidator(FeatDefinitionProvider feats) : IRecipeValidator
{
    public string Name => Pf2eRecipeValidatorNames.FeatEligibility;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var picks = new CreationPicks(
            ctx.Resolve("class") as ClassDefinition,
            ctx.Resolve("ancestry") as RaceDefinition,
            ctx.Resolve("background") as BackgroundDefinition,
            null,
            ctx.Level,
            0);
        var category = CreationSources.FeatCategory(step.Source);
        foreach (var pick in draft.GetList(step.Key))
        {
            // A name that isn't a feat at all is the options check's ("not among the options").
            if (!feats.TryGet(ctx.System, pick, out var feat))
                continue;

            if (CreationSources.FeatIneligibility(feat, category, picks) is { } why)
                yield return CreationIssue.Error(step.Key, why);
        }
    }
}

/// <summary>
/// A class that trains "Acrobatics or Athletics" (the fighter) needs one of them among the skill picks, unless the
/// background trains one already (<c>background</c>'s skill, or the one picked at <c>backgroundSkill</c>).
/// </summary>
public sealed class Pf2eClassSkillsValidator : IRecipeValidator
{
    public string Name => Pf2eRecipeValidatorNames.ClassSkills;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        if (ctx.Resolve("class") is not ClassDefinition { SkillChoices.OneOf.Count: > 0 } cls)
            yield break;

        var oneOf = cls.SkillChoices!.OneOf;
        var picks = draft.GetList(step.Key);
        var background = ctx.Resolve("background") as BackgroundDefinition;
        var fromBackground = (background?.SkillProficiencies ?? [])
            .Concat(CreationSources.BackgroundSkill(background, ctx.Resolve("backgroundSkill") as string) is { } chosen ? [chosen] : []);
        if (picks.Count > 0 && !picks.Concat(fromBackground).Any(p => oneOf.Contains(p, StringComparer.OrdinalIgnoreCase)))
            yield return CreationIssue.Error(step.Key, $"A {CreationSources.Label(cls.Name)} is trained in {string.Join(" or ", oneOf)}: pick one of them.");
    }
}
