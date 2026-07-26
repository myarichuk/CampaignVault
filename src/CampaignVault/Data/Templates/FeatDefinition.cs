namespace CampaignVault.Data.Templates;

public record FeatDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public string? Prerequisite { get; init; }
    public string? MechanicalSummary { get; init; }
    public List<string> ExtraPools { get; init; } = [];

    /// <summary>
    /// Freeform prerequisite strings (e.g. "Agility 6+"). Data round-trip only — not enforced at
    /// grant time. No attribute-comparator parser or grant-time validation hook exists yet; the
    /// LLM DM is responsible for honoring these when narrating a character taking the perk.
    /// </summary>
    public List<string> Requirements { get; init; } = [];

    public static FeatDefinition Merge(FeatDefinition child, FeatDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Prerequisite = child.Prerequisite ?? parent.Prerequisite,
            MechanicalSummary = child.MechanicalSummary ?? parent.MechanicalSummary,
            ExtraPools = child.ExtraPools.Count > 0 ? child.ExtraPools : parent.ExtraPools,
            Requirements = child.Requirements.Count > 0 ? child.Requirements : parent.Requirements,
        };
}