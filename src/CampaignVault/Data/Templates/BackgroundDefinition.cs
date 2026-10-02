namespace CampaignVault.Data.Templates;

public record BackgroundDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public List<string> SkillProficiencies { get; init; } = [];
    public List<string> ToolProficiencies { get; init; } = [];
    public List<string> Languages { get; init; } = [];
    public string? Feature { get; init; }

    /// <summary>PF2e: one of the two background boosts must go to one of these; empty means both are free.</summary>
    public List<string> Boosts { get; init; } = [];

    /// <summary>PF2e: the trained skill is one of these (Hermit: Nature or Occultism), instead of <see cref="SkillProficiencies"/>.</summary>
    public List<string> SkillOptions { get; init; } = [];

    /// <summary>PF2e: the Lore skill it trains ("Scribing Lore"), or a description of the choice.</summary>
    public string? Lore { get; init; }

    /// <summary>PF2e: the skill feat it grants (a feat template name).</summary>
    public string? SkillFeat { get; init; }

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
            Boosts = child.Boosts.Count > 0 ? child.Boosts : parent.Boosts,
            SkillOptions = child.SkillOptions.Count > 0 ? child.SkillOptions : parent.SkillOptions,
            Lore = child.Lore ?? parent.Lore,
            SkillFeat = child.SkillFeat ?? parent.SkillFeat,
        };
}