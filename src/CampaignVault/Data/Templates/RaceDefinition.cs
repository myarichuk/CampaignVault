using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

public record RaceDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public List<string> Traits { get; init; } = [];
    public Dictionary<string, int> AbilityBonuses { get; init; } = [];
    public string? Size { get; init; }
    public float? BaseSpeed { get; init; }
    public List<string> ExtraLanguages { get; init; } = [];

    /// <summary>PF2e: the ancestry's Hit Points (added once, at level 1).</summary>
    public int? Hp { get; init; }

    /// <summary>PF2e: free attribute boosts on top of the fixed ones in <see cref="AbilityBonuses"/> (a flaw is a -1 there).</summary>
    public int? FreeBoosts { get; init; }

    /// <summary>PF2e: the heritages a character of this ancestry picks one of.</summary>
    public List<HeritageDefinition> Heritages { get; init; } = [];

    /// <summary>5e: abilities of the player's choice that rise by 1 each (see <see cref="RaceAbilityChoice"/>).</summary>
    public RaceAbilityChoice? AbilityChoice { get; init; }

    /// <summary>5e: what the race does to rolls (a resistance, advantage on some saves), in the feat effect vocabulary.</summary>
    public List<FeatEffect> Effects { get; init; } = [];

    /// <summary>
    /// 5e: spells the race gives by character level (spell ids): <c>{1: [light], 3: [faerie_fire]}</c>. Cantrips join the
    /// character's cantrips, the others its known spells; how often it casts them is the race's description.
    /// </summary>
    public Dictionary<int, List<string>> Spells { get; init; } = [];

    /// <summary>5e: darkvision range in feet.</summary>
    public int? Darkvision { get; init; }

    /// <summary>5e: armor, weapon and tool proficiencies it gives.</summary>
    public ProficiencyGrants? Proficiencies { get; init; }

    /// <summary>5e: skills it makes the character proficient in.</summary>
    public List<string> Skills { get; init; } = [];

    /// <summary>5e: how many skills of the player's choice it makes the character proficient in.</summary>
    public int SkillChoices { get; init; }

    /// <summary>5e: how many feats of the player's choice it gives at level 1.</summary>
    public int BonusFeats { get; init; }

    public static RaceDefinition Merge(RaceDefinition child, RaceDefinition parent)
    {
        var merged = child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Traits = child.Traits.Count > 0 ? child.Traits : parent.Traits,
            Size = child.Size ?? parent.Size,
            BaseSpeed = child.BaseSpeed ?? parent.BaseSpeed,
            ExtraLanguages = child.ExtraLanguages.Count > 0 ? child.ExtraLanguages : parent.ExtraLanguages,
            Hp = child.Hp ?? parent.Hp,
            FreeBoosts = child.FreeBoosts ?? parent.FreeBoosts,
            Heritages = child.Heritages.Count > 0 ? child.Heritages : parent.Heritages,
            AbilityChoice = child.AbilityChoice ?? parent.AbilityChoice,
            Effects = child.Effects.Count > 0 ? child.Effects : parent.Effects,
            Spells = child.Spells.Count > 0 ? child.Spells : parent.Spells,
            Darkvision = child.Darkvision ?? parent.Darkvision,
            Proficiencies = child.Proficiencies ?? parent.Proficiencies,
            Skills = child.Skills.Count > 0 ? child.Skills : parent.Skills,
            SkillChoices = child.SkillChoices > 0 ? child.SkillChoices : parent.SkillChoices,
            BonusFeats = child.BonusFeats > 0 ? child.BonusFeats : parent.BonusFeats,
        };

        if (parent.AbilityBonuses.Count > 0)
        {
            var mergedBonuses = child.AbilityBonuses.Count > 0
                ? new Dictionary<string, int>(child.AbilityBonuses)
                : new Dictionary<string, int>();
            foreach (var (key, value) in parent.AbilityBonuses)
                mergedBonuses.TryAdd(key, value);
            merged = merged with { AbilityBonuses = mergedBonuses };
        }

        return merged;
    }
}

/// <summary>
/// 5e: <c>{ count: 2, exclude: [Charisma] }</c> is +1 to two different abilities of the player's choice, not Charisma (the
/// builder's racial ability step).
/// </summary>
public sealed record RaceAbilityChoice
{
    public int Count { get; init; }
    public List<string> Exclude { get; init; } = [];
}

/// <summary>A PF2e heritage, listed under its ancestry (<c>heritages:</c>). <see cref="Name"/> is what the character stores.</summary>
public record HeritageDefinition
{
    public string Name { get; init; } = null!;
    public string? Label { get; init; }
    public string? Description { get; init; }
}
