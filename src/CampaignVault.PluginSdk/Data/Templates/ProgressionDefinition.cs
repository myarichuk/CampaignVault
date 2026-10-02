using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Defines a single choice point at a specific level (e.g., subclass at level 3, fighting style at level 1).
/// </summary>
public record LevelUpChoiceDefinition
{
    /// <summary>Unique key for this choice (e.g., "subclass", "fightingStyle", "asiOrFeat"). Populated from the parent feature's choices map key.</summary>
    public string Key { get; init; } = null!;

    /// <summary>Human-readable prompt for the LLM. Falls back to the owning feature's name if not set.</summary>
    public string? Prompt { get; init; }

    /// <summary>Type of choice for UI/validation hints.</summary>
    public ChoiceType Type { get; init; } = ChoiceType.Enum;

    /// <summary>Whether this choice is required at this level.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Available options for enum/feat-selection choices.</summary>
    public List<ChoiceOption> Options { get; init; } = [];

    /// <summary>For AsiOrFeat choices: which abilities may be boosted. Empty means the standard six.</summary>
    public List<string> AbilityOptions { get; init; } = [];

    /// <summary>
    /// How many different options this choice takes ("choose two metamagic options" is 2). An ability score improvement
    /// ignores it (one ability +2, or two +1 each).
    /// </summary>
    public int Count { get; init; } = 1;
}

/// <summary>Type of level-up choice for rendering/validation hints.</summary>
public enum ChoiceType
{
    Enum,           // Single choice from a list (subclass, fighting style, pact boon)
    AsiOrFeat,      // Ability Score Improvement OR Feat
    SpellSelection, // Choose spells known/prepared
    FeatSelection,  // Choose a feat from available list (e.g. warlock invocations)
    FreeText,       // Open-ended (rare, for homebrew)
    SkillIncrease,  // PF2e: one skill a proficiency rank up (trained, expert; master from 7, legendary from 15)
    AttributeBoosts, // PF2e: Count different attributes, each +1 (a partial boost at +4 or more)
    SkillProficiency // 5e: Count skills the character isn't proficient in become proficient (a Lore bard's three)
}

/// <summary>An option for an enum/feat-selection choice.</summary>
public record ChoiceOption
{
    public string Id { get; init; } = null!;
    public string Label { get; init; } = null!;
    public string? Description { get; init; }

    /// <summary>Skills the option trains (a PF2e racket, druidic order or patron: "Thievery").</summary>
    public List<string> Skills { get; init; } = [];

    /// <summary>More skills of the player's choice it trains (the mastermind racket's "one of the following").</summary>
    public int ExtraSkills { get; init; }

    /// <summary>An attribute it lets the class use as its key attribute (the ruffian racket's Strength).</summary>
    public string? KeyAbility { get; init; }

    /// <summary>What picking it does to rolls (Archery: +2 to ranged weapon attacks), in the feat effect vocabulary.</summary>
    public List<FeatEffect> Effects { get; init; } = [];

    /// <summary>
    /// The features it gives by class level (a subclass's): <c>{3: [Hunter's Prey], 7: [Defensive Tactics]}</c>. A feature's
    /// own choices are asked for once the option is picked and the level reached.
    /// </summary>
    public Dictionary<int, List<FeatureDefinition>> Features { get; init; } = [];

    /// <summary>Set for options that came from a plugin or the campaign (a <see cref="ClassOptionDefinition"/>), not the shipped rules.</summary>
    public bool Homebrew { get; init; }
}

/// <summary>
/// A named class feature gained at a level, optionally gating a choice (e.g. "Martial Archetype" gates the subclass pick).
/// </summary>
public record FeatureDefinition
{
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public Dictionary<string, LevelUpChoiceDefinition> Choices { get; init; } = [];

    /// <summary>What the feature does to rolls, in the feat effect vocabulary. Anything else stays its description, for the DM.</summary>
    public List<FeatEffect> Effects { get; init; } = [];

    /// <summary>Extra hit points per character level (Draconic Resilience: 1).</summary>
    public int HpPerLevel { get; init; }

    /// <summary>Armor class without armor (Unarmored Defense: 10 + Dexterity + Constitution). The best one the character has wins.</summary>
    public UnarmoredArmorClass? UnarmoredArmorClass { get; init; }

    /// <summary>Armor, weapon and tool proficiencies it gives (a domain's heavy armor), joined to the sheet's from its level.</summary>
    public ProficiencyGrants? Proficiencies { get; init; }

    /// <summary>Resource pools it gives by name (a subclass's superiority dice), sized by the pool's own rules from its level.</summary>
    public List<string> Pools { get; init; } = [];

    /// <summary>
    /// Spells it gives by class level, always prepared and not counted against the day's picks (a domain's, an oath's, a
    /// circle's): spell ids. They join the character's prepared list when the class level is reached.
    /// </summary>
    public Dictionary<int, List<string>> Spells { get; init; } = [];

    /// <summary>
    /// Spells it adds to the class's list to choose from by class level (a patron's expanded list): spell ids. They are offered
    /// where the class's spells are, and cost a pick like any other.
    /// </summary>
    public Dictionary<int, List<string>> SpellOptions { get; init; } = [];
}

/// <summary>Armor class while wearing no armor: <see cref="Base"/> plus the modifiers of <see cref="Abilities"/>.</summary>
public record UnarmoredArmorClass
{
    public int Base { get; init; } = 10;
    public List<string> Abilities { get; init; } = [];
}

/// <summary>A feature a character has at its level: the class's own, or one an option it picked gives (<see cref="From"/>).</summary>
public sealed record GainedFeature(int Level, FeatureDefinition Feature, ChoiceOption? From);

/// <summary>A choice a character makes at a level: the class's own, or one a feature of a picked option asks (<see cref="From"/>).</summary>
public sealed record GainedChoice(int Level, LevelUpChoiceDefinition Choice, ChoiceOption? From);

/// <summary>
/// Level definition in a class progression.
/// </summary>
public record LevelDefinition
{
    public int Level { get; init; }
    public int ProficiencyBonus { get; init; } = 0; // 5e
    public List<FeatureDefinition> Features { get; init; } = [];

    // PF2e-specific
    public int? ClassFeats { get; init; }
    public int? SkillFeats { get; init; }
    public int? GeneralFeats { get; init; }
    public int? AncestryFeats { get; init; }

    /// <summary>Number of free ability boosts gained at this level (PF2e). 0/absent means none.</summary>
    public int? AbilityBoosts { get; init; }
    public int? SpellLevelGained { get; init; }

    /// <summary>Cantrips known from this level on (5e). Absent means the same as the level before.</summary>
    public int? CantripsKnown { get; init; }

    /// <summary>Spells known (known casters) or spellbook spells (wizard) from this level on (5e). Absent means the same as the level before.</summary>
    public int? SpellsKnown { get; init; }

    /// <summary>Flattened choices from every feature at this level, tagged with their choice key and a prompt.</summary>
    public List<LevelUpChoiceDefinition> Choices =>
    [
        .. Features
            .SelectMany(f => f.Choices.Select(kv => kv.Value with
            {
                Key = kv.Key,
                Prompt = kv.Value.Prompt ?? f.Name,
            }))
    ];
}

/// <summary>
/// Complete progression definition for a class.
/// </summary>
public record ProgressionDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public string ClassName { get; init; } = null!;
    public List<string> Aliases { get; init; } = [];
    public string? HitDie { get; init; }
    public CasterType? CasterType { get; init; }
    public List<string> KeyAbility { get; init; } = []; // PF2e: e.g., ["Intelligence"]
    public List<string> SavingThrows { get; init; } = [];
    public List<string> Pools { get; init; } = [];
    public Dictionary<int, LevelDefinition> Levels { get; init; } = [];

    /// <summary>
    /// Options plugins add to this class's choices, by choice key (<c>subclass</c>): filled in from the class option files
    /// after the layers resolve, never written in a progression file.
    /// </summary>
    public Dictionary<string, List<ChoiceOption>> ExtraOptions { get; init; } = [];

    /// <summary>
    /// Ids of shipped options to take away (<c>hideOptions+: [champion]</c> in a patch). A character that already picked one
    /// keeps the record, but the builder stops offering it and it gives nothing.
    /// </summary>
    public List<string> HideOptions { get; init; } = [];

    /// <summary>Prepared casters: how many spells they prepare each day. Null for known casters and non-casters.</summary>
    public PreparedSpellsDefinition? PreparedSpells { get; init; }

    /// <summary>The highest level at or below <paramref name="level"/> that sets <paramref name="pick"/>, or 0 when none does.</summary>
    public int CountAtLevel(int level, Func<LevelDefinition, int?> pick) =>
        Levels.Where(kv => kv.Key <= level && pick(kv.Value) is not null)
            .OrderByDescending(kv => kv.Key)
            .Select(kv => pick(kv.Value)!.Value)
            .FirstOrDefault();

    /// <summary>
    /// A choice's options: its own, or when it has none (a later "learn one more invocation", "two more maneuvers"), those
    /// of the same key elsewhere: in the features of the option it came from (<paramref name="from"/>, a subclass), then
    /// at another level of the class.
    /// </summary>
    public List<ChoiceOption> OptionsFor(LevelUpChoiceDefinition choice, ChoiceOption? from = null)
    {
        var own = choice.Options.Count > 0
            ? choice.Options
            : NestedChoices(from).FirstOrDefault(c => c.Key == choice.Key && c.Options.Count > 0)?.Options
                ?? Levels.Values.SelectMany(l => l.Choices).FirstOrDefault(c => c.Key == choice.Key && c.Options.Count > 0)?.Options
                ?? [];
        // Plugin options join only a choice that already offers some, so "asiOrFeat" and the like stay untouched.
        if (own.Count == 0 || !ExtraOptions.TryGetValue(choice.Key, out var extra))
            return HideOptions.Count == 0 ? own : [.. own.Where(o => !IsHidden(o))];

        return [.. own.Concat(extra.Where(e => !own.Any(o => o.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase)))).Where(o => !IsHidden(o))];
    }

    /// <summary>The choices the features of <paramref name="option"/> ask, keyed like a level's.</summary>
    private static IEnumerable<LevelUpChoiceDefinition> NestedChoices(ChoiceOption? option) =>
        option is null
            ? []
            : option.Features.Values.SelectMany(list => list)
                .SelectMany(f => f.Choices.Select(kv => kv.Value with { Key = kv.Key }));

    private bool IsHidden(ChoiceOption option) => HideOptions.Contains(option.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every feature at levels 1 to <paramref name="level"/>, in level order: the class's, then those the options
    /// <paramref name="picked"/> (level, choice key → option ids) names give, and theirs in turn.
    /// </summary>
    public IReadOnlyList<GainedFeature> FeaturesUpTo(int level, Func<int, string, IEnumerable<string>> picked) =>
        Walk(level, picked).Features;

    /// <summary>Every choice at levels 1 to <paramref name="level"/>: the class's, and those of picked options' features.</summary>
    public IReadOnlyList<GainedChoice> ChoicesUpTo(int level, Func<int, string, IEnumerable<string>> picked) =>
        Walk(level, picked).Choices;

    /// <summary>The options <paramref name="picked"/> names among the choices up to <paramref name="level"/>, each once.</summary>
    public IReadOnlyList<ChoiceOption> PickedOptions(int level, Func<int, string, IEnumerable<string>> picked) =>
        Walk(level, picked).Picked;

    private (List<GainedFeature> Features, List<GainedChoice> Choices, List<ChoiceOption> Picked) Walk(
        int level, Func<int, string, IEnumerable<string>> picked)
    {
        var features = new List<GainedFeature>();
        var choices = new List<GainedChoice>();
        var options = new List<ChoiceOption>();
        var queue = new Queue<GainedFeature>(Levels.Where(kv => kv.Key <= level).OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Features.Select(f => new GainedFeature(kv.Key, f, null))));
        // An option's feature can ask a choice whose options have features of their own; the guard stops bad data looping.
        for (var guard = 0; queue.Count > 0 && guard < 1000; guard++)
        {
            var gained = queue.Dequeue();
            features.Add(gained);
            foreach (var (key, def) in gained.Feature.Choices)
            {
                var choice = def with { Key = key, Prompt = def.Prompt ?? gained.Feature.Name };
                choices.Add(new GainedChoice(gained.Level, choice, gained.From));
                var available = OptionsFor(choice, gained.From);
                foreach (var id in picked(gained.Level, key))
                {
                    var option = available.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (option is null || options.Any(o => o.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    options.Add(option);
                    foreach (var (at, list) in option.Features.Where(kv => kv.Key <= level).OrderBy(kv => kv.Key))
                    {
                        foreach (var f in list)
                            queue.Enqueue(new GainedFeature(Math.Max(at, gained.Level), f, option));
                    }
                }
            }
        }

        return ([.. features.OrderBy(f => f.Level)], [.. choices.OrderBy(c => c.Level)], options);
    }

    public static ProgressionDefinition Merge(ProgressionDefinition child, ProgressionDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            ClassName = !string.IsNullOrEmpty(child.ClassName) ? child.ClassName : parent.ClassName,
            Aliases =
            [
                .. child.Aliases
                    .Union(parent.Aliases, StringComparer.OrdinalIgnoreCase)
            ],
            HitDie = child.HitDie ?? parent.HitDie,
            CasterType = child.CasterType ?? parent.CasterType,
            Description = child.Description ?? parent.Description,
            KeyAbility = child.KeyAbility.Count > 0 ? child.KeyAbility : parent.KeyAbility,
            SavingThrows = child.SavingThrows.Count > 0 ? child.SavingThrows : parent.SavingThrows,
            Pools = child.Pools.Count > 0 ? child.Pools : parent.Pools,
            HideOptions = [.. child.HideOptions.Union(parent.HideOptions, StringComparer.OrdinalIgnoreCase)],
            Levels = MergeLevels(child.Levels, parent.Levels),
            PreparedSpells = child.PreparedSpells ?? parent.PreparedSpells,
        };

    private static Dictionary<int, LevelDefinition> MergeLevels(
        Dictionary<int, LevelDefinition> child,
        Dictionary<int, LevelDefinition> parent)
    {
        var merged = new Dictionary<int, LevelDefinition>(parent);
        foreach (var (level, def) in child)
        {
            merged[level] = def with
            {
                Features = def.Features.Count > 0 ? def.Features : (parent.TryGetValue(level, out var p) ? p.Features : []),
            };
        }
        return merged;
    }
}

/// <summary>
/// Spells prepared per day: the <see cref="Ability"/> modifier plus the class level divided by <see cref="LevelDivisor"/>
/// (rounded down), at least 1, from <see cref="FromLevel"/> on. 5e cleric/druid/wizard: divisor 1; paladin: 2 from level 2.
/// </summary>
public record PreparedSpellsDefinition
{
    public string Ability { get; init; } = null!;
    public int LevelDivisor { get; init; } = 1;
    public int FromLevel { get; init; } = 1;

    /// <summary>The count for a class level and ability modifier, or 0 below <see cref="FromLevel"/>.</summary>
    public int CountFor(int level, int abilityModifier) =>
        level < FromLevel ? 0 : Math.Max(1, abilityModifier + level / Math.Max(1, LevelDivisor));
}

public enum CasterType { None, Full, Half, Third, Warlock, HalfRoundUp }

