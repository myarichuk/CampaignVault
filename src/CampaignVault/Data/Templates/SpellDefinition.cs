namespace CampaignVault.Data.Templates;

public record SpellDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;

    /// <summary>0 = cantrip.</summary>
    public int? Level { get; init; }

    public List<string> Classes { get; init; } = [];
    public bool? Concentration { get; init; }
    public string? CastingTime { get; init; }

    public static SpellDefinition Merge(SpellDefinition child, SpellDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Level = child.Level ?? parent.Level,
            Concentration = child.Concentration ?? parent.Concentration,
            CastingTime = child.CastingTime ?? parent.CastingTime,
            Classes = child.Classes.Count > 0 ? child.Classes : parent.Classes,
        };
}