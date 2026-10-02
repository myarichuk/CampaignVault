namespace CampaignVault.Data.Templates;

/// <summary>
/// Armor, weapon and tool proficiencies something gives (5e): a class, a class feature, a race, a background or a feat.
/// The character's sheet holds the union of every grant it has.
/// <code>
/// proficiencies: { armor: [light, medium, shields], weapons: [simple, longsword], tools: [thieves_tools] }
/// </code>
/// Armor is a category (<c>light</c>, <c>medium</c>, <c>heavy</c>, <c>shields</c>); a weapon is a category (<c>simple</c>,
/// <c>martial</c>) or an item name (<c>crossbow_hand</c>); a tool is a name.
/// </summary>
public record ProficiencyGrants
{
    public List<string> Armor { get; init; } = [];
    public List<string> Weapons { get; init; } = [];
    public List<string> Tools { get; init; } = [];
}
