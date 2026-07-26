namespace CampaignVault.Data.Templates;

public record RaceDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public List<string> Traits { get; init; } = [];
    public Dictionary<string, int> AbilityBonuses { get; init; } = [];
    public string? Size { get; init; }
    public float? BaseSpeed { get; init; }
    public List<string> ExtraLanguages { get; init; } = [];

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