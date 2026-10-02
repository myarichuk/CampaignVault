namespace CampaignVault.Data.Templates;

public record ClassDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public string? HitDie { get; init; }
    // Nullable: null means "inherit from parent"; explicit None means non-caster
    public CasterType? CasterType { get; init; }
    /// <summary>The ability the class casts with (e.g. "Intelligence"); null for non-casters or to inherit.</summary>
    public string? SpellcastingAbility { get; init; }
    public List<string> Pools { get; init; } = [];
    public List<string> SavingThrows { get; init; } = [];
    public List<string> Aliases { get; init; } = [];

    /// <summary>The level-1 "choose N skills from this list" pick (the character builder's skills step). <c>from: [any]</c> means every skill.</summary>
    public SkillChoiceDefinition? SkillChoices { get; init; }

    /// <summary>5e: the armor, weapons and tools a character starting in this class is proficient with.</summary>
    public ProficiencyGrants? Proficiencies { get; init; }

    /// <summary>5e: the smaller set a character gets when it multiclasses into this class (not its starting class).</summary>
    public ProficiencyGrants? MulticlassProficiencies { get; init; }

    public static ClassDefinition Merge(ClassDefinition child, ClassDefinition parent) =>
        child with
        {
            Proficiencies = child.Proficiencies ?? parent.Proficiencies,
            MulticlassProficiencies = child.MulticlassProficiencies ?? parent.MulticlassProficiencies,
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            HitDie = child.HitDie ?? parent.HitDie,
            CasterType = child.CasterType ?? parent.CasterType,
            SpellcastingAbility = child.SpellcastingAbility ?? parent.SpellcastingAbility,
            Description = child.Description ?? parent.Description,
            Pools = child.Pools.Count > 0 ? child.Pools : parent.Pools,
            SavingThrows = child.SavingThrows.Count > 0 ? child.SavingThrows : parent.SavingThrows,
            SkillChoices = child.SkillChoices ?? parent.SkillChoices,
            // Aliases: union so subclasses inherit parent aliases automatically
            Aliases =
            [
                .. child.Aliases
                    .Union(parent.Aliases, StringComparer.OrdinalIgnoreCase)
            ],
        };
}


/// <summary>A class's skill pick at level 1: <see cref="Count"/> skills from <see cref="From"/>.</summary>
public record SkillChoiceDefinition
{
    public int Count { get; init; }
    public List<string> From { get; init; } = [];

    /// <summary>PF2e: skills the class always trains (a wizard's Arcana); not picks.</summary>
    public List<string> Trained { get; init; } = [];

    /// <summary>PF2e: one of the picks must be one of these (a fighter's Acrobatics or Athletics).</summary>
    public List<string> OneOf { get; init; } = [];
}
