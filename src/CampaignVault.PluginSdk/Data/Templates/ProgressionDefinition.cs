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
}

/// <summary>Type of level-up choice for rendering/validation hints.</summary>
public enum ChoiceType
{
    Enum,           // Single choice from a list (subclass, fighting style, pact boon)
    AsiOrFeat,      // Ability Score Improvement OR Feat
    SpellSelection, // Choose spells known/prepared
    FeatSelection,  // Choose a feat from available list (e.g. warlock invocations)
    FreeText        // Open-ended (rare, for homebrew)
}

/// <summary>An option for an enum/feat-selection choice.</summary>
public record ChoiceOption
{
    public string Id { get; init; } = null!;
    public string Label { get; init; } = null!;
    public string? Description { get; init; }
}

/// <summary>
/// A named class feature gained at a level, optionally gating a choice (e.g. "Martial Archetype" gates the subclass pick).
/// </summary>
public record FeatureDefinition
{
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public Dictionary<string, LevelUpChoiceDefinition> Choices { get; init; } = [];
}

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

    /// <summary>Prepared casters: how many spells they prepare each day. Null for known casters and non-casters.</summary>
    public PreparedSpellsDefinition? PreparedSpells { get; init; }

    /// <summary>The highest level at or below <paramref name="level"/> that sets <paramref name="pick"/>, or 0 when none does.</summary>
    public int CountAtLevel(int level, Func<LevelDefinition, int?> pick) =>
        Levels.Where(kv => kv.Key <= level && pick(kv.Value) is not null)
            .OrderByDescending(kv => kv.Key)
            .Select(kv => pick(kv.Value)!.Value)
            .FirstOrDefault();

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

