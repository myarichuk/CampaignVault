namespace CampaignVault.Data.Templates;

public record BackgroundDefinition : RulesetTemplate
{
    public string System { get; init; } = default!;
    public List<string> SkillProficiencies { get; init; } = [];
    public List<string> ToolProficiencies { get; init; } = [];
    public List<string> Languages { get; init; } = [];
    public string? Feature { get; init; }

    public static BackgroundDefinition Merge(BackgroundDefinition child, BackgroundDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Feature = child.Feature ?? parent.Feature,
            SkillProficiencies = child.SkillProficiencies.Count > 0
                ? child.SkillProficiencies
                : parent.SkillProficiencies,
            ToolProficiencies = child.ToolProficiencies.Count > 0
                ? child.ToolProficiencies
                : parent.ToolProficiencies,
            Languages = child.Languages.Count > 0 ? child.Languages : parent.Languages,
        };
}