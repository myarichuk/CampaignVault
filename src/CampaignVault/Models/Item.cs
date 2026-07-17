namespace CampaignVault.Models;

public class Item : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = default!;
    
    public float[]? SemanticVector { get; set; }
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public string HolderId { get; set; } = default!; // Character.Id, Location.Id, or Item.Id

    public int Quantity { get; set; } = 1;

    public string? CurrentState { get; set; }

    public List<string> DistinctiveFeatures { get; set; } = [];

    public ItemCategory CoreCategory { get; set; }

    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Maps a tag/feature/state text (as it appears in Tags/DistinctiveFeatures/CurrentState) to the
    /// event ID(s) that established it — objective ground truth. Engine-populated only; not an
    /// LLM-settable commit field.
    /// </summary>
    public Dictionary<string, List<string>> TagProvenance { get; set; } = [];

    public Dictionary<string, object> Properties { get; set; } = [];

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Zones this item can be equipped into (e.g. Torso, MainHand, Ring). Empty = not equippable.
    /// Set at creation via upsert_item; state changes go through item_equip/item_unequip.
    /// </summary>
    public List<EquipZone> EquipZones { get; set; } = [];

    /// <summary>
    /// Which layer this item occupies within its EquipZones. Items in different layers on the
    /// same zone can coexist (e.g. Torso/Armor + Torso/Outer); items in the same zone+layer conflict.
    /// Null when EquipZones is empty (not equippable).
    /// </summary>
    public EquipLayer? EquipLayer { get; set; }

    /// <summary>Whether this item is currently worn/wielded. Set only via item_equip/item_unequip.</summary>
    public bool IsEquipped { get; set; }

    /// <summary>When true and this item occupies MainHand, it also occupies OffHand (blocks shields/other off-hand items).</summary>
    public bool TwoHanded { get; set; }

    /// <summary>Container capacity (e.g. number of items or volume units). Null = unstructured, unlimited (current behavior).</summary>
    public int? Capacity { get; set; }

    /// <summary>Unit label for <see cref="Capacity"/> (e.g. "items", "liters"). Narrative only.</summary>
    public string? CapacityUnit { get; set; }

    /// <summary>Maximum charges/doses this item can hold (water gourd, healing ointment, reagent vial). Null = not a limited-use item.</summary>
    public int? MaxCharges { get; set; }

    /// <summary>Current remaining charges. Null until first item_use, which lazy-inits it to MaxCharges.</summary>
    public int? CurrentCharges { get; set; }

    /// <summary>Unit label for charges (e.g. "doses", "sips", "uses"). Narrative only.</summary>
    public string? ChargeUnit { get; set; }

    /// <summary>
    /// Ambient lifecycle metadata for left-behind objects (the porridge-plate case). Null = no
    /// expiry tracked (current behavior). Set/refreshed via item_update's ambientPersistenceNote/
    /// ambientExpiresAtDay fields; the engine only ever flips PressureSurfaced, never moves/archives/
    /// deletes the item itself.
    /// </summary>
    public AmbientPersistence? Persistence { get; set; }

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Items may be shareable across camps in some designs.)
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}

public enum ItemCategory
{
    Weapon,
    Armor,
    Clothing,
    Container,
    Consumable,
    Tool,
    Material,
    Valuable,
    Document,
    Key,
    Other
}

/// <summary>Body/hand slot an equippable item can occupy. An item may list several (e.g. a two-handed weapon lists MainHand only but sets TwoHanded).</summary>
public enum EquipZone
{
    Head,
    Face,
    Neck,
    Torso,
    Back,
    Waist,
    Hands,
    Wrists,
    Legs,
    Feet,
    MainHand,
    OffHand,
    Ring,
    Accessory
}

/// <summary>Layer within an EquipZone. Distinct layers on the same zone coexist (robe over chainmail); same zone+layer conflicts.</summary>
public enum EquipLayer
{
    Base,
    Armor,
    Outer,
    Held
}

/// <summary>
/// Ambient item lifecycle metadata, LLM-authored at drop-time (mirrors StatusEffect.RecoveryHint /
/// StatusChange's "LLM is sole author of expiration" convention).
/// </summary>
public class AmbientPersistence
{
    /// <summary>LLM-authored narrative note about why/how this item was left behind. Narrative only.</summary>
    public string? Note { get; set; }

    /// <summary>LLM-authored campaign day (CampaignTime.TotalDaysElapsed + N) after which the engine nags about this item's fate.</summary>
    public float? ExpiresAtDay { get; set; }

    /// <summary>Engine-managed only (parallels TagProvenance's engine-only fields). True once AmbientItemDecayRule has surfaced the expiry pressure; never set by the LLM directly.</summary>
    public bool PressureSurfaced { get; set; }
}
