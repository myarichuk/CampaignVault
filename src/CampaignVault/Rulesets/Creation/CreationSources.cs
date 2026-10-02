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
    ProgressionDefinitionProvider progressions,
    NamedPowerProvider? powers = null)
{
    public const string Races = "races";
    public const string Classes = "classes";
    public const string Backgrounds = "backgrounds";
    public const string ClassSkills = "classSkills";
    public const string Skills = "skills";
    public const string Spells = "spells";
    public const string Feats = "feats";
    public const string Creatures = "creatures";
    public const string Companions = "companions";
    public const string StartingEquipment = "startingEquipment";
    public const string Abilities = "abilities";
    /// <summary>A stat block field source only (a companion's creature type), never a step's.</summary>
    public const string CreatureTypes = "creatureTypes";

    /// <summary>A stat block field source only (a companion's saving throws): 5e's six abilities, PF2e's three saves.</summary>
    public const string Saves = "saves";

    /// <summary>PF2e: the chosen ancestry's heritages.</summary>
    public const string Heritages = "heritages";

    /// <summary>PF2e: the chosen background's two skills when it trains one of them (Hermit: Nature or Occultism).</summary>
    public const string BackgroundSkills = "backgroundSkills";

    /// <summary>Skills neither the chosen class nor the background trains already (PF2e's extra trained skills).</summary>
    public const string UntrainedSkills = "untrainedSkills";

    /// <summary>PF2e boosts: the abilities the chosen ancestry doesn't boost already (its free boosts).</summary>
    public const string AncestryBoosts = "ancestryBoosts";

    /// <summary>PF2e boosts: the six abilities (the background's two; <c>pf2e.boosts</c> checks one is from its pair).</summary>
    public const string BackgroundBoosts = "backgroundBoosts";

    /// <summary>PF2e: the chosen class's key attributes (its progression's <c>keyAbility</c>).</summary>
    public const string KeyAbilities = "keyAbilities";

    /// <summary>PF2e feats by category, for the draft's level, class and ancestry (<see cref="FeatsFor"/>).</summary>
    public const string AncestryFeats = "ancestryFeats";
    public const string ClassFeats = "classFeats";
    public const string SkillFeats = "skillFeats";
    public const string GeneralFeats = "generalFeats";

    /// <summary>Named powers (<see cref="NamedPowerDefinition"/>) by type: gods, patrons, bloodlines. None ship; plugins add them.</summary>
    public const string Deities = "deities";
    public const string Patrons = "patrons";
    public const string Lineages = "lineages";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Races, Classes, Backgrounds, ClassSkills, Skills, Spells, Feats, Creatures, Companions, StartingEquipment, Abilities,
        Heritages, BackgroundSkills, UntrainedSkills, AncestryBoosts, BackgroundBoosts, KeyAbilities,
        AncestryFeats, ClassFeats, SkillFeats, GeneralFeats, Deities, Patrons, Lineages,
    };

    /// <summary>The named power type a powers source lists (<c>deities</c> → deity), or null for another source.</summary>
    public static string? PowerType(string? source) => source?.ToLowerInvariant() switch
    {
        "deities" => NamedPowerDefinition.Deity,
        "patrons" => NamedPowerDefinition.Patron,
        "lineages" => NamedPowerDefinition.Lineage,
        _ => null,
    };

    /// <summary>The feat category a feats source lists (PF2e <c>category:</c>), or null for another source.</summary>
    public static string? FeatCategory(string? source) => source?.ToLowerInvariant() switch
    {
        "ancestryfeats" => "ancestry",
        "classfeats" => "class",
        "skillfeats" => "skill",
        "generalfeats" => "general",
        _ => null,
    };

    /// <summary>The SRD 5.1 creature types.</summary>
    public static readonly IReadOnlyList<string> Dnd5eCreatureTypes =
    [
        "Aberration", "Beast", "Celestial", "Construct", "Dragon", "Elemental", "Fey", "Fiend", "Giant", "Humanoid",
        "Monstrosity", "Ooze", "Plant", "Undead",
    ];

    /// <summary>PF2e (Monster Core) creature traits that say what a creature is.</summary>
    public static readonly IReadOnlyList<string> Pf2eCreatureTypes =
    [
        "Aberration", "Animal", "Astral", "Beast", "Celestial", "Construct", "Dragon", "Elemental", "Ethereal", "Fey",
        "Fiend", "Fungus", "Giant", "Humanoid", "Monitor", "Ooze", "Plant", "Spirit", "Undead",
    ];

    public static readonly IReadOnlyList<string> Pf2eSaveNames = ["Fortitude", "Reflex", "Will"];

    public static readonly IReadOnlyList<string> AbilityNames =
        ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"];

    public RaceDefinitionProvider RaceProvider => races;
    public ClassDefinitionProvider ClassProvider => classes;
    public BackgroundDefinitionProvider BackgroundProvider => backgrounds;
    public FeatDefinitionProvider FeatProvider => feats;
    public ProgressionDefinitionProvider ProgressionProvider => progressions;

    /// <summary>The template a pickOne choice names, for path references (<c>class.skillChoices.count</c>); null for sources without templates.</summary>
    public RulesetTemplate? Template(string system, string? source, string id) => source?.ToLowerInvariant() switch
    {
        "races" => races.TryGet(system, id, out var race) ? race : null,
        "classes" => classes.TryResolveClass(system, id, out var cls) ? cls : null,
        "backgrounds" => backgrounds.TryGet(system, id, out var bg) ? bg : null,
        "feats" => feats.TryGet(system, id, out var feat) ? feat : null,
        "creatures" => creatures.TryGet(system, id, out var creature) ? creature : null,
        "deities" or "patrons" or "lineages" => Power(system, source, id),
        _ => null,
    };

    /// <summary>The named power with this id, when it is of the type the source lists.</summary>
    public NamedPowerDefinition? Power(string system, string? source, string id) =>
        PowerType(source) is { } type && powers is not null && powers.TryGet(system, id, out var power)
            && power is not null && power.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && FeatEffectRules.PluginAvailable(power.Requires)
            ? power
            : null;

    /// <summary>The options of a source for a system, for the draft's picks so far (class, ancestry, background, level).</summary>
    public IReadOnlyList<CreationOption> Options(string system, string source, CreationPicks picks)
    {
        var classTemplate = picks.Class;
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
            case "companions":
                return
                [
                    .. creatures.GetCreaturesForSystem(system).Values
                        .Where(c => c.Companion)
                        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(c => new CreationOption(c.Name, c.Name, c.Description) { Values = c.StatBlock }),
                ];
            case "skills":
                return [.. SkillNames(system).Select(s => new CreationOption(s, s))];
            case "classskills":
                return ClassSkillOptions(system, classTemplate);
            case "abilities":
                return [.. AbilityNames.Select(a => new CreationOption(a, a))];
            case "spells":
                return classTemplate is null ? [] : SpellOptions(system, classTemplate, picks, CharacterClassFeatures.ExpandedSpells(picks.Granted, picks.Level));
            case "heritages":
                return [.. (picks.Race?.Heritages ?? []).Select(h => new CreationOption(h.Name, h.Label ?? Label(h.Name), h.Description))];
            case "backgroundskills":
                return [.. (picks.Background?.SkillOptions ?? []).Select(s => new CreationOption(s, s))];
            case "untrainedskills":
            {
                var trained = TrainedSkills(picks).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return [.. SkillNames(system).Where(s => !trained.Contains(s)).Select(s => new CreationOption(s, s))];
            }
            case "ancestryboosts":
            {
                var boosted = (picks.Race?.AbilityBonuses ?? []).Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return [.. AbilityNames.Where(a => !boosted.Contains(a)).Select(a => new CreationOption(a, a))];
            }
            case "backgroundboosts":
                return [.. AbilityNames.Select(a => new CreationOption(a, a))];
            case "keyabilities":
                // The class's own, and any a class feature picked allows (the ruffian racket's Strength).
                return classTemplate is not null && progressions.TryGetProgression(system, classTemplate.Name, out var progression)
                    ? [.. progression.KeyAbility.Concat(picks.Granted.Select(o => o.KeyAbility).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Select(a => new CreationOption(a, a))]
                    : [];
            case "deities" or "patrons" or "lineages":
                return
                [
                    .. (powers?.GetPowersForSystem(system).Values ?? [])
                        .Where(p => PowerType(source) is { } type && p.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                        .Where(p => FeatEffectRules.PluginAvailable(p.Requires))
                        .Where(p => p.Classes.Count == 0 || (classTemplate is not null && p.Classes.Contains(classTemplate.Name, StringComparer.OrdinalIgnoreCase)))
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(p => new CreationOption(p.Name, p.Label ?? Label(p.Name), p.Description) { Homebrew = p.Homebrew }),
                ];
            case "ancestryfeats" or "classfeats" or "skillfeats" or "generalfeats":
                return [.. FeatsFor(system, source, picks).Select(FeatOption)];
            default:
                // startingEquipment: no packages in the data yet; the DM equips to fit.
                return [];
        }
    }

    /// <summary>
    /// The names a <c>modifiers</c> or <c>choice</c> stat block field may use, from its source; empty for a source with
    /// no fixed names (or none in this system).
    /// </summary>
    public static IReadOnlyList<string> FieldNames(string system, string? source)
    {
        var pf2e = system.Equals(RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(source, Skills, StringComparison.OrdinalIgnoreCase))
            // A PF2e creature lists Perception with its skills, and the PF2e rules read it from the skill modifiers.
            return pf2e ? [.. SkillNames(system), "Perception"] : SkillNames(system);
        if (string.Equals(source, Abilities, StringComparison.OrdinalIgnoreCase))
            return AbilityNames;
        if (string.Equals(source, Saves, StringComparison.OrdinalIgnoreCase))
            return pf2e ? Pf2eSaveNames : AbilityNames;
        if (string.Equals(source, CreatureTypes, StringComparison.OrdinalIgnoreCase))
            return system.Equals(RulesetSystem.Dnd5e, StringComparison.OrdinalIgnoreCase) ? Dnd5eCreatureTypes : pf2e ? Pf2eCreatureTypes : [];
        return [];
    }

    public static IReadOnlyList<string> SkillNames(string system) => system.ToLowerInvariant() switch
    {
        RulesetSystem.Dnd5e => [.. Dnd5eSkillTable.GoverningAbility.Keys.Order(StringComparer.OrdinalIgnoreCase)],
        RulesetSystem.Pathfinder2e => [.. Pf2eSkillTable.KeyAbility.Keys.Order(StringComparer.OrdinalIgnoreCase)],
        _ => [],
    };

    /// <summary>
    /// The background skill picked from the background's options, in its spelling; null when it isn't one of them (a pick
    /// left over from another background).
    /// </summary>
    public static string? BackgroundSkill(BackgroundDefinition? background, string? picked) =>
        picked is null ? null : background?.SkillOptions.FirstOrDefault(o => o.Equals(picked, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The skills the draft is trained in before its own skill picks: the class's fixed ones, the background's, the
    /// background skill picked from its options, and those a class feature picked trains (a racket's).
    /// </summary>
    public static IEnumerable<string> TrainedSkills(CreationPicks picks) =>
        (picks.Class?.SkillChoices?.Trained ?? [])
            .Concat(picks.Background?.SkillProficiencies ?? [])
            .Concat(picks.BackgroundSkill is { } chosen ? [chosen] : [])
            .Concat(picks.Granted.SelectMany(o => o.Skills));

    /// <summary>
    /// The feats a PF2e feats source offers: that category, at or below the draft's level, and for a class or ancestry
    /// feat, the chosen class or ancestry's. Without the class (or ancestry) chosen, none of those are offered.
    /// </summary>
    public IEnumerable<FeatDefinition> FeatsFor(string system, string source, CreationPicks picks)
    {
        var category = FeatCategory(source);
        return feats.GetFeatsForSystem(system).Values
            .Where(f => FeatEffectRules.PluginAvailable(f.Requires))
            .Where(f => FeatIneligibility(f, category, picks) is null)
            .OrderBy(f => f.Level ?? 0)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Why a feat can't be taken from a feats source with this category, or null when it can.</summary>
    public static string? FeatIneligibility(FeatDefinition feat, string? category, CreationPicks picks)
    {
        var name = Label(feat.Name);
        if (category is not null && !string.Equals(feat.Category, category, StringComparison.OrdinalIgnoreCase))
            return $"{name} is in the {feat.Category ?? "uncategorized"} feats; this step takes {category} feats.";
        if ((feat.Level ?? 0) > picks.Level)
            return $"{name} is a level {feat.Level} feat; the character is level {picks.Level}.";
        if (category == "class" && !feat.Classes.Contains(picks.Class?.Name ?? "", StringComparer.OrdinalIgnoreCase))
            return picks.Class is null ? "Choose a class before its feats." : $"{name} isn't a {Label(picks.Class.Name)} feat.";
        if (category == "ancestry" && !feat.Ancestries.Contains(picks.Race?.Name ?? "", StringComparer.OrdinalIgnoreCase))
            return picks.Race is null ? "Choose an ancestry before its feats." : $"{name} isn't a {Label(picks.Race.Name)} feat.";
        return null;
    }

    /// <summary>
    /// The feats an ability score improvement may take instead (5e): the system's feats without a PF2e category, by name.
    /// </summary>
    public IReadOnlyList<CreationOption> AsiFeats(string system) =>
    [
        .. feats.GetFeatsForSystem(system).Values
            .Where(f => FeatEffectRules.PluginAvailable(f.Requires) && string.IsNullOrWhiteSpace(f.Category))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(FeatOption),
    ];

    /// <summary>What the slots of a feat taken at an improvement offer: the feat, and spells of some lists at a level.</summary>
    public FeatChoiceSource FeatChoices(string system) => new(
        id => feats.TryGet(system, id, out var feat) ? feat : null,
        (lists, level) =>
        [
            .. lists.SelectMany(list => spells.QuerySpells(system, list, classProvider: classes))
                .Where(s => (s.Level ?? 0) == level && FeatEffectRules.PluginAvailable(s.Requires))
                .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new CreationOption(s.Name, Label(s.Name), s.Description) { Homebrew = s.Homebrew }),
        ]);

    /// <summary>A feat as an option: its summary, and the prerequisite text (its checkable part is feat.prerequisites's).</summary>
    private static CreationOption FeatOption(FeatDefinition feat)
    {
        var summary = feat.MechanicalSummary ?? feat.Description;
        var description = string.IsNullOrWhiteSpace(feat.Prerequisite) ? summary : $"Prerequisite: {feat.Prerequisite}. {summary}";
        return new CreationOption(feat.Name, Label(feat.Name), description?.Trim(), feat.Level is { } level ? $"level {level}" : null) { Homebrew = feat.Homebrew };
    }

    private static IReadOnlyList<CreationOption> ClassSkillOptions(string system, ClassDefinition? classTemplate)
    {
        var from = classTemplate?.SkillChoices?.From ?? [];
        if (from.Any(s => s.Equals("any", StringComparison.OrdinalIgnoreCase)))
            return [.. SkillNames(system).Select(s => new CreationOption(s, s))];

        return [.. from.Select(s => new CreationOption(s, s))];
    }

    private IReadOnlyList<CreationOption> SpellOptions(string system, ClassDefinition classTemplate, CreationPicks picks, IReadOnlyList<string> expanded)
    {
        var maxSpellLevel = picks.MaxSpellLevel;
        // A subclass that casts learns from another class's list, its leveled spells limited to its schools until it
        // has an any-school pick.
        var casting = picks.OptionCasting;
        var listClass = casting?.List ?? classTemplate.Name;
        var anySchool = casting is null || casting.Schools.Count == 0 || casting.AnySchoolAt.Any(l => l <= picks.Level);
        var options = spells.QuerySpells(system, listClass, classProvider: classes)
            .Where(s => (s.Level ?? 0) <= maxSpellLevel)
            .ToList();
        // The campaign's own spells for this class (HomebrewScope) join the shipped ones, tagged homebrew.
        foreach (var own in HomebrewScope.Current?.SpellsFor(system) ?? [])
        {
            if ((own.Level ?? 0) <= maxSpellLevel && SpellDefinitionProvider.SpellMatchesClass(own, classTemplate.Name, system, classes)
                && FeatEffectRules.PluginAvailable(own.Requires) && !options.Any(o => o.Name.Equals(own.Name, StringComparison.OrdinalIgnoreCase)))
                options.Add(own);
        }

        // A picked subclass's extra spells (a patron's list) join the class's own.
        foreach (var id in expanded)
        {
            if (spells.TryGet(system, id, out var extra) && extra is not null && (extra.Level ?? 0) <= maxSpellLevel
                && !options.Any(o => o.Name.Equals(extra.Name, StringComparison.OrdinalIgnoreCase)))
                options.Add(extra);
        }

        return
        [
            .. options
                .Where(s => anySchool || (s.Level ?? 0) == 0 || InSchools(s, casting!))
                .Select(s => new CreationOption(
                    s.Name,
                    Label(s.Name),
                    casting is { Schools.Count: > 0 } && (s.Level ?? 0) > 0 && !InSchools(s, casting)
                        ? $"{s.Description} (outside {string.Join("/", casting.Schools)}: one of the any-school picks)".Trim()
                        : s.Description,
                    (s.Level ?? 0) == 0 ? SpellGroups.Cantrips : SpellGroups.Known) { Homebrew = s.Homebrew }),
        ];
    }

    private static bool InSchools(SpellDefinition spell, OptionSpellcasting casting) =>
        spell.School is { } school && casting.Schools.Contains(school, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<CreationOption> Templates<T>(IEnumerable<T> templates) where T : RulesetTemplate =>
    [
        .. templates
            .Where(t => FeatEffectRules.PluginAvailable(t.Requires))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new CreationOption(t.Name, Label(t.Name), t.Description) { Homebrew = t.Homebrew }),
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

/// <summary>
/// What a draft has chosen so far that a source's options depend on: its class, race or ancestry, background (and the
/// background skill picked from its options), level, and the highest spell level its class can cast.
/// </summary>
public sealed record CreationPicks(
    ClassDefinition? Class,
    RaceDefinition? Race,
    BackgroundDefinition? Background,
    string? BackgroundSkill,
    int Level,
    int MaxSpellLevel)
{
    /// <summary>The class feature options picked so far that give something (a racket's skills and key attribute).</summary>
    public IReadOnlyList<ChoiceOption> Granted { get; init; } = [];

    /// <summary>
    /// 5e: the spellcasting a picked option gives a class without its own (a subclass that casts), or null. Only used when
    /// the class itself casts nothing.
    /// </summary>
    public OptionSpellcasting? OptionCasting =>
        Class?.CasterType is null or CasterType.None
            ? Granted.Select(o => o.Spellcasting).OfType<OptionSpellcasting>().FirstOrDefault()
            : null;
}
