namespace CampaignVault.Models;

public class Item : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = default!;
    
    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText()
    {
        var detailLines = ItemDetails
            .Where(d => !d.IsRetired)
            .Select(d => $"{d.Name}: {d.Status ?? ""} - {d.Description}");
        return string.Join("\n", new[] { $"{Name}\n{Description}" }.Concat(detailLines));
    }

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

    /// <summary>
    /// Persistent, granular state on this item (scratches, stains, secret compartments, custom
    /// pockets) — durable across sessions, unlike Tags/DistinctiveFeatures which are flat labels.
    /// Upserted via item_update's upsertItemDetail/retireItemDetailId commit fields.
    /// </summary>
    public List<ItemDetail> ItemDetails { get; set; } = [];

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Zones this item can be equipped into (e.g. Torso, MainHand, Ring). Empty = not equippable.
    /// Set at creation via world_build; state changes go through item_equip/item_unequip.
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

    /// <summary>
    /// Null (default) = shares the flat zone+layer capacity pool (today's behavior: two items in the
    /// same zone+layer always conflict). Non-null = carves out an independent sub-pool within that
    /// zone+layer keyed by this value — items with different StackGroups on the same zone+layer
    /// coexist (modular armor parts: "pauldron-left" + "pauldron-right" both on Torso/Armor), while
    /// items sharing the same StackGroup still conflict at the zone's capacity (can't wear two
    /// "pauldron-left"s). A StackGroup-tagged item never competes with an ungrouped (null) item on
    /// the same zone+layer. Set via world_build; never interpreted by AC/dex-cap math.
    /// </summary>
    public string? StackGroup { get; set; }

    /// <summary>
    /// To equip this item, the character must already have at least one other currently-equipped item
    /// carrying each of these tags (AND-across-list, OR-within-tag — matches natural "requires chest
    /// armor AND a belt" phrasing). Zone/layer-independent; used for attachment/prerequisite
    /// relationships (metal pauldrons need chest armor for their straps). Null/empty = no prerequisite.
    /// </summary>
    public List<string>? RequiresEquippedTags { get; set; }

    /// <summary>
    /// This item cannot be equipped while any currently-equipped item carries any of these tags
    /// (OR-semantics). Zone/layer-independent — catches cross-zone incompatibilities the slot system
    /// can never see (a ceremonial robe incompatible with a wielded weapon). Null/empty = no
    /// incompatibility declared.
    /// </summary>
    public List<string>? IncompatibleWithEquippedTags { get; set; }

    /// <summary>
    /// Purely cosmetic/narrative tags (e.g. "form-fitting", "plunging-neckline") visible to the DM-LLM
    /// when narrating or judging social scenes. Never read by AC/warmth/movement resolution. May
    /// overlap the crowd-scoring visualTags vocabulary (e.g. "well_armed") where relevant, but is not
    /// itself auto-scored.
    /// </summary>
    public List<string>? VisualTags { get; set; }

    /// <summary>
    /// Short narrative description of how this item reads on the wearer ("cut low across the chest,
    /// clearly tailored for court, not combat"). Purely descriptive; never affects mechanics.
    /// </summary>
    public string? AppearanceNote { get; set; }

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

/// <summary>
/// A persistent, granular detail on an Item — a scratch, stain, secret compartment, custom
/// pocket, glyph, etc. Ground-truth world state; never deleted (see <see cref="IsRetired"/>).
/// </summary>
public class ItemDetail : IHasSemanticVector
{
    /// <summary>Engine-assigned, e.g. "detail-" + Guid. Canonical identity — never matched by Name.</summary>
    public string Id { get; set; } = default!;

    /// <summary>Short label, free text (e.g. "Hidden compartment"). Display/LLM-reference only.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Full narrative description of the detail's current state.</summary>
    public string Description { get; set; } = default!;

    /// <summary>Optional short current-status label, distinct from the full description.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// DM-only guidance on how to narrate or adjudicate this detail (e.g. suggested DC, discovery
    /// conditions). Never shown to players; excluded from semantic embedding text.
    /// </summary>
    public string? Intent { get; set; }

    /// <summary>Soft-delete flag. Retired details are kept (not removed) so memory references stay resolvable.</summary>
    public bool IsRetired { get; set; }

    /// <summary>What caused/created this detail.</summary>
    public ItemDetailOrigin? Origin { get; set; }

    /// <summary>Characters who caused or witnessed this detail; each pushes a memory node on upsert.</summary>
    public List<ItemDetailParticipant> Participants { get; set; } = [];

    /// <summary>
    /// Optional id of whatever this detail currently physically anchors the item to — a location
    /// (a rope's other end lashed to a column), another item (leashed to a stake), or a character
    /// (tied to a handler). Distinct from Origin: Origin is what caused the detail; TetheredToId is
    /// what it's currently attached to (may differ, e.g. a snapped tether keeps Origin but clears
    /// this). Purely descriptive — the engine does not enforce movement/range constraints from it;
    /// the DM-LLM reads it and adjudicates (e.g. blocking travel beyond tether length).
    /// </summary>
    public string? TetheredToId { get; set; }

    /// <summary>In-game day (CampaignTime.TotalDaysElapsed) this detail was first created.</summary>
    public int CreatedOnDay { get; set; }

    /// <summary>In-game day this detail was last touched.</summary>
    public int UpdatedOnDay { get; set; }

    /// <summary>
    /// Days of no updates before ItemDetailStalenessRule nudges the DM-LLM to reconsider this detail.
    /// Set by whoever authored the detail — a punctured waterskin or a scabbing wound plausibly
    /// changes within a day or two, while a scorch mark or a crater might not be worth revisiting for
    /// months. Null falls back to ItemDetailStalenessRule's global default (60 days).
    /// </summary>
    public int? ReviewIntervalDays { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}: {Status ?? ""} - {Description}";
}

public enum ItemDetailOriginType { Actor, Event, Hazard, Item, Environmental, Unknown }

public class ItemDetailOrigin
{
    /// <summary>Character/Event/Item id, if the origin resolves to a known entity.</summary>
    public string? Id { get; set; }

    public ItemDetailOriginType Type { get; set; } = ItemDetailOriginType.Unknown;

    /// <summary>Free-text fallback when Id is absent or the origin is untyped/ephemeral.</summary>
    public string? Description { get; set; }
}

public enum ItemDetailParticipantRole { Caused, Witnessed }

public class ItemDetailParticipant
{
    public string Id { get; set; } = default!;
    public ItemDetailParticipantRole Role { get; set; }
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
