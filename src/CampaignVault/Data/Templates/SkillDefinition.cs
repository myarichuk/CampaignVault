namespace CampaignVault.Data.Templates;

/// <summary>
/// Reference skill metadata (governing attribute), surfaced via get_system_handbook for
/// discoverability. Skills themselves are freeform strings flowing through
/// SystemExtension.Skills/action.Parameters["skill"] — this is documentation, not validation.
/// </summary>
public record SkillDefinition : RulesetTemplate
{
    public string System { get; init; } = default!;
    public string? Attribute { get; init; }

    public static SkillDefinition Merge(SkillDefinition child, SkillDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Attribute = child.Attribute ?? parent.Attribute,
        };
}
