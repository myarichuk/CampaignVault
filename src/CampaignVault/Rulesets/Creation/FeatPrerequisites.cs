using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// What a draft has that feat prerequisites ask about, as the path <c>sheet</c> resolves it (<c>sheet.skillRanks.Athletics</c>):
/// its abilities (5e scores, PF2e modifiers, level choices included), skill ranks, feats and class feature picks.
/// </summary>
public sealed record CreationSheet(
    IReadOnlyDictionary<string, int> Abilities,
    IReadOnlyDictionary<string, Pf2eProficiencyRank> SkillRanks,
    IReadOnlyList<string> Feats,
    IReadOnlyList<string> ClassFeatures);

/// <summary>Checks a feat's <see cref="FeatDefinition.Prerequisites"/> against the draft's <see cref="CreationSheet"/>.</summary>
public static class FeatPrerequisiteCheck
{
    /// <summary>The prerequisites the draft doesn't meet, as text ("trained in Athletics"). Empty when it meets them all.</summary>
    public static IReadOnlyList<string> Unmet(FeatDefinition feat, CreationContext ctx) =>
        ctx.Resolve("sheet") is CreationSheet sheet ? Unmet(feat, sheet, ctx.System) : [];

    /// <summary>The same check against a sheet read from an existing character (a level-up), not a draft.</summary>
    public static IReadOnlyList<string> Unmet(FeatDefinition feat, CreationSheet sheet, string system) =>
        feat.Prerequisites.Count == 0 ? [] : [.. feat.Prerequisites.Where(p => !Met(p, sheet, feat.Name)).Select(p => Describe(p, system))];

    /// <summary>"Grappler needs Strength 13." / "Twin Riposte needs Twin Parry and trained in Acrobatics."</summary>
    public static string? Message(FeatDefinition feat, CreationContext ctx) =>
        Needs(feat, Unmet(feat, ctx));

    public static string? Message(FeatDefinition feat, CreationSheet sheet, string system) =>
        Needs(feat, Unmet(feat, sheet, system));

    private static string? Needs(FeatDefinition feat, IReadOnlyList<string> unmet) =>
        unmet.Count > 0 ? $"{CreationSources.Label(feat.Name)} needs {string.Join(" and ", unmet)}." : null;

    private static bool Met(FeatPrerequisite p, CreationSheet sheet, string self)
    {
        if (p.AnyOf.Count > 0)
            return p.AnyOf.Any(a => Met(a, sheet, self));
        if (p.Skill is { } skill)
            return sheet.SkillRanks.TryGetValue(skill, out var rank) && rank >= Rank(p.Rank);
        if (p.Ability is { } ability)
            return sheet.Abilities.TryGetValue(ability, out var value) && value >= (p.Min ?? 0);
        if (p.Feat is { } feat)
            return !feat.Equals(self, StringComparison.OrdinalIgnoreCase) && sheet.Feats.Contains(feat, StringComparer.OrdinalIgnoreCase);
        if (p.ClassFeature is { } option)
            return sheet.ClassFeatures.Contains(option, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private static Pf2eProficiencyRank Rank(string? rank) =>
        Enum.TryParse<Pf2eProficiencyRank>(rank, ignoreCase: true, out var parsed) ? parsed : Pf2eProficiencyRank.Trained;

    private static string Describe(FeatPrerequisite p, string system)
    {
        if (p.AnyOf.Count > 0)
            return "one of " + string.Join(", ", p.AnyOf.Select(a => Describe(a, system)));
        if (p.Skill is { } skill)
            return $"{(p.Rank ?? "trained").ToLowerInvariant()} in {skill}";
        if (p.Ability is { } ability)
            return system.Equals(RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase) ? $"{ability} +{p.Min}" : $"{ability} {p.Min}";
        if (p.Feat is { } feat)
            return CreationSources.Label(feat);
        return p.ClassFeature is { } option ? LevelChoiceSlots.Humanize(option) : "?";
    }
}

/// <summary>Each feat picked meets the prerequisites its data can check (<see cref="FeatDefinition.Prerequisites"/>).</summary>
public sealed class FeatPrerequisitesValidator(FeatDefinitionProvider feats) : IRecipeValidator
{
    public string Name => RecipeValidatorNames.FeatPrerequisites;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        foreach (var pick in draft.GetList(step.Key))
        {
            if (feats.TryGet(ctx.System, pick, out var feat) && FeatPrerequisiteCheck.Message(feat, ctx) is { } why)
                yield return CreationIssue.Error(step.Key, why);
        }
    }
}
