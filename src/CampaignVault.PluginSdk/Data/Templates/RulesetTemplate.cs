namespace CampaignVault.Data.Templates;

public abstract record RulesetTemplate
{
    public string Name { get; init; } = null!;
    public List<string> Inherits { get; init; } = [];
    public string? Description { get; init; }
}
