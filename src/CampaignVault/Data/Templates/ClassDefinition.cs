namespace CampaignVault.Data.Templates;

public record ClassDefinition : RulesetTemplate
{
    public string System { get; init; } = default!;
    public string? HitDie { get; init; }
    // Nullable: null means "inherit from parent"; explicit None means non-caster
    public CasterType? CasterType { get; init; }
    public List<string> Pools { get; init; } = [];
    public List<string> SavingThrows { get; init; } = [];
    public List<string> Aliases { get; init; } = [];

    public static ClassDefinition Merge(ClassDefinition child, ClassDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            HitDie = child.HitDie ?? parent.HitDie,
            CasterType = child.CasterType ?? parent.CasterType,
            Description = child.Description ?? parent.Description,
            Pools = child.Pools.Count > 0 ? child.Pools : parent.Pools,
            SavingThrows = child.SavingThrows.Count > 0 ? child.SavingThrows : parent.SavingThrows,
            // Aliases: union so subclasses inherit parent aliases automatically
            Aliases = child.Aliases
                .Union(parent.Aliases, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
}

public enum CasterType { None, Full, Half, Third, Warlock, HalfRoundUp }
