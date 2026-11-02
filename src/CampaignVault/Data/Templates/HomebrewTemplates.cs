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
                // The ruleset's budget (skill count, gold...) needs the system: the repository checks it (BackgroundProblems).
                HomebrewKinds.Background => Describe(Read<BackgroundDefinition>(yaml), _ => []),
                _ => Describe(Read<RaceDefinition>(yaml), _ => []),
            };
        }
        catch (Exception ex)
        {
            return (null, [$"The YAML doesn't read as a {canonical}: {ex.Message}"]);
        }
    }

    /// <summary>Most gold a homebrew dnd5e background may start with (SRD acolyte: 15 gp).</summary>
    public const int MaxDnd5eBackgroundGold = 25;

    /// <summary>Most starting-gear entries a homebrew background may list.</summary>
    public const int MaxBackgroundEquipment = 8;

    private static readonly string[] Abilities = ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"];

    /// <summary>The background a background YAML reads as, or null when it doesn't.</summary>
    public static BackgroundDefinition? ReadBackground(string yaml)
    {
        try
        {
            return Read<BackgroundDefinition>(yaml);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What keeps a homebrew background inside the ruleset's budget, so one the DM writes for a character's backstory is
    /// worth about what a shipped one is. dnd5e (SRD 5.1 acolyte): two skills, two tools or languages between them, a
    /// little gear and at most <see cref="MaxDnd5eBackgroundGold"/> gp. pf2e (Player Core): two boosts (at least one from
    /// a pair of abilities, or both free), one trained skill or a choice of two, a Lore and a skill feat, no gold.
    /// <paramref name="itemExists"/> says whether an <c>item:</c> id names an item template; null skips that check.
    /// </summary>
    public static IReadOnlyList<string> BackgroundProblems(BackgroundDefinition background, string system, Func<string, bool>? itemExists = null)
    {
        var problems = new List<string>();
        var skills = Rulesets.Creation.CreationSources.SkillNames(system);
        bool KnownSkill(string s) => skills.Count == 0 || skills.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var skill in background.SkillProficiencies.Concat(background.SkillOptions).Where(s => !KnownSkill(s)))
            problems.Add($"'{skill}' is not a {system} skill. Use one of: {string.Join(", ", skills)}.");
        if (background.SkillProficiencies.Concat(background.SkillOptions).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != background.SkillProficiencies.Count + background.SkillOptions.Count)
            problems.Add("A skill is listed twice.");
        if (string.IsNullOrWhiteSpace(background.Description))
            problems.Add("Give it a description: who someone of this background was before adventuring.");

        if (background.Equipment.Count > MaxBackgroundEquipment)
            problems.Add($"At most {MaxBackgroundEquipment} starting items (got {background.Equipment.Count}).");
        foreach (var item in background.Equipment)
        {
            if (string.IsNullOrWhiteSpace(item.Item) == string.IsNullOrWhiteSpace(item.Name))
                problems.Add("Each starting item is either { item: <template id> } or { name: <plain item> }.");
            else if (item.Quantity is < 1 or > 20)
                problems.Add($"Starting item '{item.Item ?? item.Name}': quantity is 1 to 20.");
            else if (!string.IsNullOrWhiteSpace(item.Item) && itemExists is not null && !itemExists(item.Item!))
                problems.Add($"No item template '{item.Item}'. Use {{ name: ... }} for a plain item.");
        }

        if (system.Equals(RulesetSystem.Dnd5e, StringComparison.OrdinalIgnoreCase))
        {
            if (background.SkillProficiencies.Count != 2)
                problems.Add($"A dnd5e background grants exactly 2 skill proficiencies (got {background.SkillProficiencies.Count}).");
            if (background.ToolProficiencies.Count + background.Languages.Count > 2)
                problems.Add("A dnd5e background grants at most 2 tool proficiencies and languages between them.");
            if (background.Gold is < 0 or > MaxDnd5eBackgroundGold)
                problems.Add($"A dnd5e background starts with 0 to {MaxDnd5eBackgroundGold} gp (got {background.Gold}).");
            if (background.Boosts.Count > 0 || background.SkillOptions.Count > 0 || background.Lore is not null || background.SkillFeat is not null)
                problems.Add("boosts, skillOptions, lore and skillFeat are pf2e fields; a dnd5e background doesn't use them.");
        }
        else if (system.Equals(RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var boost in background.Boosts.Where(b => !Abilities.Contains(b, StringComparer.OrdinalIgnoreCase)))
                problems.Add($"'{boost}' is not an ability.");
            if (background.Boosts.Count is not (0 or 2) || background.Boosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != background.Boosts.Count)
                problems.Add("boosts lists two different abilities (one boost goes to one of them, the other is free), or none for two free boosts.");
            var trained = background.SkillProficiencies.Count + (background.SkillOptions.Count > 0 ? 1 : 0);
            if (trained != 1 || background.SkillOptions.Count is not (0 or 2))
                problems.Add("A pf2e background trains one skill: skillProficiencies with one skill, or skillOptions with two to choose from.");
            if (string.IsNullOrWhiteSpace(background.Lore))
                problems.Add("A pf2e background trains a Lore skill: set lore (e.g. \"Mercantile Lore\").");
            if (string.IsNullOrWhiteSpace(background.SkillFeat))
                problems.Add("A pf2e background grants a skill feat: set skillFeat to a feat id (e.g. assurance).");
            if (background.Gold != 0)
                problems.Add("A pf2e background starts with no gold of its own.");
            if (background.ToolProficiencies.Count > 0 || background.Languages.Count > 0)
                problems.Add("toolProficiencies and languages are dnd5e fields; a pf2e background doesn't use them.");
        }

        return problems;
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
    public static string Id(string campaign, string kind, string system, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Slug(name).Length == 0)
            throw new ArgumentException($"Homebrew {kind} needs a name with letters or digits (got '{name}').", nameof(name));
        return $"homebrew/{campaign}/{HomebrewKinds.Canonical(kind)}/{system}/{Slug(name)}".ToLowerInvariant();
    }

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
