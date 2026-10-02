namespace CampaignVault.Data.Templates;

/// <summary>
/// A named patron of a character: a god, a warlock's patron, a bloodline. Nothing is shipped, since every name is
/// somebody's setting; a plugin or the DM supplies them, in <c>RulesetData/&lt;system&gt;/powers/</c>.
/// <code>
/// name: the_lantern_keeper
/// type: deity               # deity | patron | lineage
/// label: The Lantern Keeper
/// classes: [cleric]         # who may take it; empty means any class
/// offers: [life, light]     # the class choice's options it joins to: a cleric's domains, a warlock's patron kinds
/// narrows: { font: [healingFont] }   # more choices it narrows, by choice key (optional)
/// </code>
/// A recipe step with the matching source (<c>deities</c>, <c>patrons</c>, <c>lineages</c>) lists them. Once one is
/// picked, the class choice named by <see cref="Choice"/> offers only the <see cref="Offers"/> options (all of them
/// when none of those exist for the class).
/// </summary>
public record NamedPowerDefinition : RulesetTemplate
{
    public const string Deity = "deity";
    public const string Patron = "patron";
    public const string Lineage = "lineage";

    public string System { get; init; } = null!;

    /// <summary><see cref="Deity"/>, <see cref="Patron"/> or <see cref="Lineage"/>.</summary>
    public string Type { get; init; } = Deity;

    public string? Label { get; init; }

    /// <summary>The classes (progression names) that may take it; empty for any.</summary>
    public List<string> Classes { get; init; } = [];

    /// <summary>The class choice <see cref="Offers"/> narrows (<c>subclass</c> for domains and patrons).</summary>
    public string Choice { get; init; } = "subclass";

    /// <summary>The ids of the choice's options it joins: the cleric domains of a god, the patron kind of a warlock's patron.</summary>
    public List<string> Offers { get; init; } = [];

    /// <summary>
    /// More choices it narrows, by choice key: <c>{ domain: [healing, sun], font: [healingFont] }</c> for a PF2e deity,
    /// whose domains and divine font are choices of the cleric.
    /// </summary>
    public Dictionary<string, List<string>> Narrows { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The option ids it offers for a class choice, or empty when it says nothing about that choice.</summary>
    public IReadOnlyList<string> OffersFor(string choiceKey) =>
        choiceKey.Equals(Choice, StringComparison.OrdinalIgnoreCase)
            ? [.. Offers.Concat(Narrows.GetValueOrDefault(choiceKey) ?? [])]
            : Narrows.GetValueOrDefault(choiceKey) ?? [];

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Deity, Patron, Lineage };

    public static NamedPowerDefinition Merge(NamedPowerDefinition child, NamedPowerDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Label = child.Label ?? parent.Label,
            Classes = child.Classes.Count > 0 ? child.Classes : parent.Classes,
            Offers = child.Offers.Count > 0 ? child.Offers : parent.Offers,
            Narrows = child.Narrows.Count > 0 ? child.Narrows : parent.Narrows,
        };
}
