namespace CampaignVault.Data.Templates;

/// <summary>
/// Reference weapon stat block (damage, skill, damage type) used as a fallback when an attack
/// names a weapon that has no matching Item in the campaign (e.g. a quick NPC-vs-NPC combat
/// where nobody bothered to author an Item document). See WeaponParameterResolver.
/// </summary>
public record WeaponDefinition : RulesetTemplate
{
    public string System { get; init; } = default!;
    public string? Skill { get; init; }
    public string? Damage { get; init; }
    public string? DamageType { get; init; }
    public int? Weight { get; init; }
    public string? Rarity { get; init; }

    public static WeaponDefinition Merge(WeaponDefinition child, WeaponDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Skill = child.Skill ?? parent.Skill,
            Damage = child.Damage ?? parent.Damage,
            DamageType = child.DamageType ?? parent.DamageType,
            Weight = child.Weight ?? parent.Weight,
            Rarity = child.Rarity ?? parent.Rarity,
        };
}
