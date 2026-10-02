using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

/// <summary>Reads and checks the YAML of a campaign's homebrew template before it is saved.</summary>
public static class HomebrewTemplates
{
    /// <summary>
    /// Parses <paramref name="yaml"/> as the template type <paramref name="kind"/> names. Returns its name and the problems
    /// that would keep it from working; a template with problems is not saved.
    /// </summary>
    public static (string? Name, IReadOnlyList<string> Problems) Check(string kind, string? yaml)
    {
        if (HomebrewKinds.Canonical(kind) is not { } canonical)
            return (null, [$"Unknown kind '{kind}'. Use {string.Join(", ", HomebrewKinds.All)}."]);
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, ["yaml is empty."]);

        try
        {
            return canonical switch
            {
                HomebrewKinds.ClassOption => Describe(Read<ClassOptionDefinition>(yaml), t =>
                [
                    .. string.IsNullOrWhiteSpace(t.Class) ? ["A class option needs 'class:' (the class it is an option of)."] : Array.Empty<string>(),
                ]),
                HomebrewKinds.Power => Describe(Read<NamedPowerDefinition>(yaml), t =>
                [
                    .. NamedPowerDefinition.Types.Contains(t.Type) ? Array.Empty<string>() : [$"A power's type is deity, patron or lineage, not '{t.Type}'."],
                ]),
                _ => Describe(Read<RaceDefinition>(yaml), _ => []),
            };
        }
        catch (Exception ex)
        {
            return (null, [$"The YAML doesn't read as a {canonical}: {ex.Message}"]);
        }
    }

    /// <summary>The class a class option's YAML names, or null when it doesn't read.</summary>
    public static string? ClassOf(string yaml)
    {
        try
        {
            return Read<ClassOptionDefinition>(yaml)?.Class;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The document id of a campaign's homebrew template; saving the same kind, system and name again replaces it.</summary>
    public static string Id(string campaign, string kind, string system, string name) =>
        $"homebrew/{campaign}/{HomebrewKinds.Canonical(kind)}/{system}/{Slug(name)}".ToLowerInvariant();

    /// <summary>A template name as it appears in a document id: lower case, letters and digits, single dashes.</summary>
    public static string Slug(string name) =>
        string.Join('-', System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(part => part.Length > 0));

    /// <summary>A campaign's own feat document as the template the builder and the rules read; shows as homebrew.</summary>
    public static FeatDefinition AsTemplate(CustomFeat feat) =>
        new FeatDefinition
        {
            Name = feat.Name,
            System = feat.System ?? string.Empty,
            Description = feat.Description,
            Prerequisite = feat.Prerequisite,
            MechanicalSummary = feat.MechanicalSummary,
            CastingWaivers = [.. feat.CastingWaivers],
            Effects = [.. feat.Effects],
            Requires = feat.Requires,
            Adjudicated = feat.Adjudicated,
            Classes = [.. feat.Classes],
            Level = feat.Level,
        }.FromCampaign();

    public static SpellDefinition AsTemplate(CustomSpell spell) =>
        new SpellDefinition
        {
            Name = spell.Name,
            System = spell.System ?? string.Empty,
            Description = spell.Description,
            Level = spell.Level,
            Classes = [.. spell.Classes],
            Concentration = spell.Concentration,
            CastingTime = spell.CastingTime,
            Verbal = spell.Verbal,
            Somatic = spell.Somatic,
            Material = spell.Material,
            MaterialText = spell.MaterialText,
            MaterialCost = spell.MaterialCost,
            MaterialConsumed = spell.MaterialConsumed,
        }.FromCampaign();

    private static T FromCampaign<T>(this T template) where T : RulesetTemplate
    {
        template.Source = HomebrewSnapshot.Source;
        return template;
    }

    private static T? Read<T>(string yaml) where T : RulesetTemplate =>
        new RulesetTemplateLoader<T>(string.Empty, typeof(T).Assembly, "campaign").Parse(yaml, "campaign homebrew");

    private static (string? Name, IReadOnlyList<string> Problems) Describe<T>(T? template, Func<T, IEnumerable<string>> check)
        where T : RulesetTemplate
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Name))
            return (null, ["The template needs a 'name:'."]);
        if (template.PatchTarget != null)
            return (template.Name, ["A campaign template is a whole template, not a 'patches:' file."]);
        return (template.Name, [.. check(template)]);
    }
}
