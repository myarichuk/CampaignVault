using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// Where a recipe step's options come from (<see cref="CreationStep.Source"/>). Each source is a template provider or
/// a fixed table; templates gated on a plugin that isn't loaded (<c>requires:</c>) are never offered.
/// </summary>
public sealed class CreationSources(
    RaceDefinitionProvider races,
    ClassDefinitionProvider classes,
    BackgroundDefinitionProvider backgrounds,
    FeatDefinitionProvider feats,
    SpellDefinitionProvider spells,
    CreatureDefinitionProvider creatures,
    ProgressionDefinitionProvider progressions)
{
    public const string Races = "races";
    public const string Classes = "classes";
    public const string Backgrounds = "backgrounds";
    public const string ClassSkills = "classSkills";
    public const string Skills = "skills";
    public const string Spells = "spells";
    public const string Feats = "feats";
    public const string Creatures = "creatures";
    public const string StartingEquipment = "startingEquipment";
    public const string Abilities = "abilities";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Races, Classes, Backgrounds, ClassSkills, Skills, Spells, Feats, Creatures, StartingEquipment, Abilities,
    };

    public static readonly IReadOnlyList<string> AbilityNames =
        ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"];

    public RaceDefinitionProvider RaceProvider => races;
    public ClassDefinitionProvider ClassProvider => classes;
    public ProgressionDefinitionProvider ProgressionProvider => progressions;

    /// <summary>The template a pickOne choice names, for path references (<c>class.skillChoices.count</c>); null for sources without templates.</summary>
    public RulesetTemplate? Template(string system, string? source, string id) => source?.ToLowerInvariant() switch
    {
        "races" => races.TryGet(system, id, out var race) ? race : null,
        "classes" => classes.TryResolveClass(system, id, out var cls) ? cls : null,
        "backgrounds" => backgrounds.TryGet(system, id, out var bg) ? bg : null,
        "feats" => feats.TryGet(system, id, out var feat) ? feat : null,
        "creatures" => creatures.TryGet(system, id, out var creature) ? creature : null,
        _ => null,
    };

    /// <summary>
    /// The options of a source for a system. <paramref name="classTemplate"/> is the draft's chosen class (classSkills,
    /// spells); <paramref name="maxSpellLevel"/> caps the leveled spells offered.
    /// </summary>
    public IReadOnlyList<CreationOption> Options(string system, string source, ClassDefinition? classTemplate, int maxSpellLevel)
    {
        switch (source.ToLowerInvariant())
        {
            case "races":
                return Templates(races.GetRacesForSystem(system).Values);
            case "classes":
                return Templates(classes.GetClassesForSystem(system).Values);
            case "backgrounds":
                return Templates(backgrounds.GetBackgroundsForSystem(system).Values);
            case "feats":
                return Templates(feats.GetFeatsForSystem(system).Values);
            case "creatures":
                return Templates(creatures.GetCreaturesForSystem(system).Values);
            case "skills":
                return [.. SkillNames(system).Select(s => new CreationOption(s, s))];
            case "classskills":
                return ClassSkillOptions(system, classTemplate);
            case "abilities":
                return [.. AbilityNames.Select(a => new CreationOption(a, a))];
            case "spells":
                return classTemplate is null ? [] : SpellOptions(system, classTemplate, maxSpellLevel);
            default:
                // startingEquipment: no packages in the data yet; the DM equips to fit.
                return [];
        }
    }

    public static IReadOnlyList<string> SkillNames(string system) => system.ToLowerInvariant() switch
    {
        RulesetSystem.Dnd5e => [.. Dnd5eSkillTable.GoverningAbility.Keys.Order(StringComparer.OrdinalIgnoreCase)],
        RulesetSystem.Pathfinder2e => [.. Pf2eSkillTable.KeyAbility.Keys.Order(StringComparer.OrdinalIgnoreCase)],
        _ => [],
    };

    private static IReadOnlyList<CreationOption> ClassSkillOptions(string system, ClassDefinition? classTemplate)
    {
        var from = classTemplate?.SkillChoices?.From ?? [];
        if (from.Any(s => s.Equals("any", StringComparison.OrdinalIgnoreCase)))
            return [.. SkillNames(system).Select(s => new CreationOption(s, s))];

        return [.. from.Select(s => new CreationOption(s, s))];
    }

    private IReadOnlyList<CreationOption> SpellOptions(string system, ClassDefinition classTemplate, int maxSpellLevel) =>
    [
        .. spells.QuerySpells(system, classTemplate.Name, classProvider: classes)
            .Where(s => (s.Level ?? 0) <= maxSpellLevel)
            .Select(s => new CreationOption(
                s.Name,
                Label(s.Name),
                s.Description,
                (s.Level ?? 0) == 0 ? SpellGroups.Cantrips : SpellGroups.Known)),
    ];

    private static IReadOnlyList<CreationOption> Templates<T>(IEnumerable<T> templates) where T : RulesetTemplate =>
    [
        .. templates
            .Where(t => FeatEffectRules.PluginAvailable(t.Requires))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new CreationOption(t.Name, Label(t.Name), t.Description)),
    ];

    /// <summary>"half_orc" → "Half Orc".</summary>
    public static string Label(string name) =>
        string.Join(' ', name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>The option groups of a spells step, and its choice's parts.</summary>
public static class SpellGroups
{
    public const string Cantrips = "cantrips";
    public const string Known = "known";
    public const string Prepared = "prepared";
}
