using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

/// <summary>
/// A homebrew/SRD item *template* — "what a Wakizashi is" — as opposed to <see cref="Item"/>,
/// which is a campaign-scoped RavenDB instance ("Bob's Wakizashi"). Not restricted to weapons/armor:
/// Category + the open Properties bag cover outfits, tools, consumables, artifacts, etc. uniformly.
/// </summary>
public record ItemDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;

    public string? Category { get; init; }

    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// Open bag for category-specific mechanical data (damage, bonus, range, weight, cost, etc.),
    /// keyed loosely by convention — mirrors <see cref="Item.Properties"/> so a definition and the
    /// instance created from it speak the same property vocabulary.
    /// </summary>
    public Dictionary<string, object> Properties { get; init; } = [];

    /// <summary>Zones an item created from this template can be equipped into. Null/empty = not equippable.</summary>
    public List<string>? EquipZones { get; init; }

    /// <summary>Layer within EquipZones (Base/Armor/Outer/Held). See <see cref="Item.EquipLayer"/>.</summary>
    public string? EquipLayer { get; init; }

    /// <summary>When true and this item occupies MainHand, it also occupies OffHand.</summary>
    public bool? TwoHanded { get; init; }

    /// <summary>See <see cref="Item.StackGroup"/>.</summary>
    public string? StackGroup { get; init; }

    /// <summary>See <see cref="Item.RequiresEquippedTags"/>.</summary>
    public List<string>? RequiresEquippedTags { get; init; }

    /// <summary>See <see cref="Item.IncompatibleWithEquippedTags"/>.</summary>
    public List<string>? IncompatibleWithEquippedTags { get; init; }

    public static ItemDefinition Merge(ItemDefinition child, ItemDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Category = child.Category ?? parent.Category,
            Tags = child.Tags.Count > 0 ? child.Tags : parent.Tags,
            Properties = MergeProperties(parent.Properties, child.Properties),
            EquipZones = child.EquipZones is { Count: > 0 } ? child.EquipZones : parent.EquipZones,
            EquipLayer = child.EquipLayer ?? parent.EquipLayer,
            TwoHanded = child.TwoHanded ?? parent.TwoHanded,
            StackGroup = child.StackGroup ?? parent.StackGroup,
            RequiresEquippedTags = child.RequiresEquippedTags is { Count: > 0 } ? child.RequiresEquippedTags : parent.RequiresEquippedTags,
            IncompatibleWithEquippedTags = child.IncompatibleWithEquippedTags is { Count: > 0 } ? child.IncompatibleWithEquippedTags : parent.IncompatibleWithEquippedTags,
        };

    private static Dictionary<string, object> MergeProperties(
        Dictionary<string, object> parent, Dictionary<string, object> child)
    {
        var merged = new Dictionary<string, object>(parent, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in child)
            merged[key] = value;
        return merged;
    }
}
