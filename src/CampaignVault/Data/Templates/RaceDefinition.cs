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

/// <summary>A PF2e heritage, listed under its ancestry (<c>heritages:</c>). <see cref="Name"/> is what the character stores.</summary>
public record HeritageDefinition
{
    public string Name { get; init; } = null!;
    public string? Label { get; init; }
    public string? Description { get; init; }
}
