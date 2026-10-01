using CampaignVault.Rulesets.Creation;

namespace CampaignVault.Data.Templates;

/// <summary>
/// The character builder's steps for one creation kind (<c>RulesetData/&lt;system&gt;/creation/pc.yaml</c>, named
/// <c>pc</c> or <c>companion</c>). A plugin adds a step with <c>patches: pc</c> and <c>steps+:</c> (placed by the
/// step's <c>after:</c>), or removes one with <c>steps-:</c> (by key).
/// </summary>
public record CreationRecipe : RulesetTemplate
{
    public string? System { get; init; }
    public List<CreationStep> Steps { get; init; } = [];

    public static CreationRecipe Merge(CreationRecipe child, CreationRecipe parent) =>
        child with
        {
            System = child.System ?? parent.System,
            Description = child.Description ?? parent.Description,
            Steps = child.Steps.Count > 0 ? child.Steps : parent.Steps,
        };
}

/// <summary>
/// A stat block the builder edits field by field (<c>RulesetData/&lt;system&gt;/statblocks/companion.yaml</c>). The
/// client draws a generic editor from it.
/// </summary>
public record StatBlockSchema : RulesetTemplate
{
    public string? System { get; init; }
    public List<StatBlockField> Fields { get; init; } = [];

    public static StatBlockSchema Merge(StatBlockSchema child, StatBlockSchema parent) =>
        child with
        {
            System = child.System ?? parent.System,
            Description = child.Description ?? parent.Description,
            Fields = child.Fields.Count > 0 ? child.Fields : parent.Fields,
        };
}

/// <summary>One stat block field, keyed by the system-stats field it writes (JSON name). <see cref="Type"/> is int, text, list, abilities or attacks.</summary>
public record StatBlockField
{
    public string Key { get; init; } = null!;
    public string? Label { get; init; }
    public string Type { get; init; } = "text";
    public int? Min { get; init; }
    public int? Max { get; init; }

    /// <summary>Fields with the same group are drawn together (e.g. defense).</summary>
    public string? Group { get; init; }
}
