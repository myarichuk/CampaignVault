namespace CampaignVault.Models;

using System.ComponentModel;
using System.Text.Json.Serialization;

/// <summary>
/// Tool-facing request for upsert_character. Mirrors <see cref="Character"/>, but declares
/// the rich sub-object fields as nullable so omitting them in a partial-update call preserves
/// the existing stored values instead of blanking them to defaults.
/// </summary>
public class CharacterUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? ClassLevel { get; set; }

    public int CurrentHp { get; set; }

    public int MaxHp { get; set; }

    public string? Notes { get; set; }

    [Description("Current transient appearance (e.g. 'blood-streaked, one pauldron strap loose'). Omit to preserve the character's existing appearance.")]
    public string? CurrentAppearance { get; set; }

    [Description("Short visual state tags (e.g. 'bloody', 'disheveled', 'unarmed'). Omit to preserve the character's existing tags.")]
    public List<string>? VisualTags { get; set; }

    [Description("Permanent distinguishing features (scars, tattoos). Omit to preserve the character's existing features.")]
    public List<string>? DistinctiveFeatures { get; set; }

    public bool KeepAlive { get; set; }

    public bool IsPc { get; set; }

    public bool IsPartyCompanion { get; set; }

    public Schedule? Schedule { get; set; }

    public string? CurrentLocationId { get; set; }

    public string? CurrentActivity { get; set; }

    [Description("Omit to preserve the character's existing psychology profile. Provide to replace it wholesale.")]
    public PsychologyProfile? Psychology { get; set; }

    [Description("Omit to preserve the character's existing social profile. Provide to replace it wholesale.")]
    public SocialProfile? Social { get; set; }

    [Description("Omit to preserve the character's existing needs profile. Provide to replace it wholesale.")]
    public NeedsProfile? Needs { get; set; }

    [Description("Omit to preserve the character's existing ruleset stats. Provide to replace it wholesale.")]
    public SystemExtension? SystemStats { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_location. Mirrors <see cref="Location"/>, but declares
/// the rich collection/dictionary fields as nullable so omitting them in a partial-update call
/// preserves the existing stored values instead of blanking them to defaults.
/// </summary>
public class LocationUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public LocationType Type { get; set; } = LocationType.Building;

    public string? ParentLocationId { get; set; }

    [Description("Only used when creating a new location. The ID of the location you are coming from — the engine automatically creates two-way exits linking them. Ignored on update.")]
    public string? ConnectedFromLocationId { get; set; }

    [Description("Only used together with connectedFromLocationId. Describes the exit from the connected location into this one (e.g., 'A wooden trapdoor leading down').")]
    public string? ConnectionDescription { get; set; }

    [Description("Omit to preserve the location's existing exits. Provide to replace them wholesale.")]
    public List<LocationExit>? Exits { get; set; }

    [Description("Omit to preserve the location's existing points of interest. Provide to replace them wholesale.")]
    public List<string>? PointsOfInterest { get; set; }

    [Description("Omit to preserve existing point-of-interest details. Provide to replace them wholesale.")]
    [JsonPropertyName("pointOfInterestDetails")]
    public Dictionary<string, string>? PointOfInterestDetails { get; set; }

    public string? AmbientCrowd { get; set; }

    public int? LastVisitedDay { get; set; }

    [Description("Omit to preserve existing metadata. Provide to replace it wholesale.")]
    public Dictionary<string, object>? Metadata { get; set; }

    public string? ControllingFactionId { get; set; }

    public string? CurrentState { get; set; }

    [Description("Narrative danger modifier (-50 to +50), used to seed random encounters. Omit to preserve the existing value on update.")]
    public int? DangerModifier { get; set; }

    [Description("Set true to hide this location from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    [Description("Climate zone for weather/temperature simulation. Omit to inherit from the nearest ParentLocationId ancestor with one set (defaults to Temperate if none in the chain). Omit on update to preserve the existing value.")]
    public ClimateZone? ClimateZone { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_lore. Mirrors <see cref="Lore"/>, but declares
/// Tags/Keywords as nullable so omitting them in a partial-update call preserves the
/// existing stored values instead of blanking them to defaults.
/// </summary>
public class LoreUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    [Description("Omit to preserve the lore entry's existing tags. Provide to replace them wholesale.")]
    public List<string>? Tags { get; set; }

    [Description("Omit to preserve the lore entry's existing keywords. Provide to replace them wholesale.")]
    public List<string>? Keywords { get; set; }

    public string? Category { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_item. Mirrors <see cref="Item"/>, but declares
/// Tags/DistinctiveFeatures/Properties as nullable so omitting them in a partial-update call
/// preserves the existing stored values instead of blanking them to defaults.
/// </summary>
public class ItemUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string HolderId { get; set; } = null!;

    public int Quantity { get; set; } = 1;

    public string? CurrentState { get; set; }

    [Description("Omit to preserve the item's existing distinctive features. Provide to replace them wholesale.")]
    public List<string>? DistinctiveFeatures { get; set; }

    public ItemCategory CoreCategory { get; set; }

    [Description("Omit to preserve existing tags; provide to replace wholesale. Open-carry/concealed convention: tag the container, not contents. See get_help topic=visual-sandbox.")]
    public List<string>? Tags { get; set; }

    [Description("Omit to preserve the item's existing properties. Provide to replace them wholesale.")]
    public Dictionary<string, object>? Properties { get; set; }

    [Description("Set true to hide this item from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    [Description("Omit to preserve the item's existing equip zones. Provide to replace them wholesale. Empty list = not equippable.")]
    public List<EquipZone>? EquipZones { get; set; }

    [Description("Which layer this item occupies within its EquipZones (Base/Armor/Outer/Held). Required (alongside EquipZones) for the item to be equippable.")]
    public EquipLayer? EquipLayer { get; set; }

    [Description("Set true when this item occupies MainHand and should also block OffHand (two-handed weapons). Omit to preserve the existing value on update.")]
    public bool? TwoHanded { get; set; }

    [Description("Set true to mark starting gear as already worn so AC/WarmthRating reflect it immediately at character creation, without a separate item_equip commit. State changes after creation go through item_equip/item_unequip instead. Omit to preserve the existing value on update.")]
    public bool? IsEquipped { get; set; }

    [Description("Container capacity (e.g. number of items or volume units). Null = unstructured, unlimited. Omit to preserve the existing value on update.")]
    public int? Capacity { get; set; }

    [Description("Unit label for Capacity (e.g. \"items\", \"liters\"). Omit to preserve the existing value on update.")]
    public string? CapacityUnit { get; set; }

    [Description("Maximum charges/doses this item can hold (water gourd, healing ointment, reagent vial). Omit to preserve the existing value on update.")]
    public int? MaxCharges { get; set; }

    [Description("Unit label for charges (e.g. \"doses\", \"sips\", \"uses\"). Omit to preserve the existing value on update.")]
    public string? ChargeUnit { get; set; }

    [Description("Null (default) = shares the flat zone+layer capacity pool. Non-null carves out an independent sub-slot within that zone+layer keyed by this value — items with different StackGroups on the same zone+layer coexist (modular armor parts: \"pauldron-left\" + \"pauldron-right\"), while items sharing the same StackGroup still conflict. Omit to preserve the existing value on update.")]
    public string? StackGroup { get; set; }

    [Description("To equip this item, the character must already have at least one other equipped item carrying each of these tags (AND-across-list). Zone/layer-independent prerequisite/attachment relationship (e.g. pauldrons require a chest-armor-tagged item). Omit to preserve the existing value on update.")]
    public List<string>? RequiresEquippedTags { get; set; }

    [Description("This item cannot be equipped while any currently-equipped item carries any of these tags (OR-semantics). Zone/layer-independent incompatibility (e.g. a loincloth incompatible with a legwear-outer-tagged item). Omit to preserve the existing value on update.")]
    public List<string>? IncompatibleWithEquippedTags { get; set; }

    [Description("Purely cosmetic/narrative tags visible to the DM-LLM when narrating or judging social scenes (e.g. \"form-fitting\", \"plunging-neckline\"). Never affects mechanics. Omit to preserve the existing value on update.")]
    public List<string>? VisualTags { get; set; }

    [Description("Short narrative description of how this item reads on the wearer. Purely descriptive; never affects mechanics. Omit to preserve the existing value on update.")]
    public string? AppearanceNote { get; set; }

    [Description("Optional durable details to seed on a NEW item only (ignored if the item already exists — use item_update's upsertItemDetail for existing items). id/participants are ignored here (no in-fiction moment yet); follow up with item_update if you need a participant memory push. See get_help topic=visual-sandbox.")]
    public List<ItemDetailUpsertRequest>? ItemDetails { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_plot_thread. Mirrors <see cref="PlotThread"/>, but declares
/// Clues/ForeshadowingHooks/InvolvedEntityIds as nullable so omitting them in a partial-update call
/// (e.g. bumping TensionLevel) preserves the existing stored values instead of blanking them.
/// </summary>
public class PlotThreadUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Summary { get; set; }

    public PlotThreadState State { get; set; } = PlotThreadState.Active;

    public int TensionLevel { get; set; }

    [Description("Omit to preserve the thread's existing clues. Provide to replace them wholesale.")]
    public List<PlotClue>? Clues { get; set; }

    [Description("Omit to preserve the thread's existing involved entity IDs. Provide to replace them wholesale.")]
    public List<string>? InvolvedEntityIds { get; set; }

    public string? ResolutionCondition { get; set; }

    [Description("Omit to preserve the thread's existing foreshadowing hooks. Provide to replace them wholesale.")]
    public List<string>? ForeshadowingHooks { get; set; }

    public string? DmNotes { get; set; }

    public int? DeadlineDay { get; set; }

    public bool IsPlayerVisible { get; set; }

    [Description("Set true to hide this plot thread from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_creature. Mirrors <see cref="CustomCreature"/>, but declares
/// Skills/Abilities as nullable so omitting them in a partial-update call preserves the
/// existing stored values instead of blanking them to defaults.
/// </summary>
public class CustomCreatureUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string System { get; set; }

    public string? Description { get; set; }

    public int? Level { get; set; }

    public string? ChallengeRating { get; set; }

    public int? Hp { get; set; }

    public int? Defense { get; set; }

    [Description("Omit to preserve the creature's existing skills. Provide to replace them wholesale.")]
    public List<string>? Skills { get; set; }

    [Description("Omit to preserve the creature's existing abilities. Provide to replace them wholesale.")]
    public List<string>? Abilities { get; set; }

    [Description("Set true to hide this creature template from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_faction. Mirrors <see cref="Faction"/>, but declares
/// list/dictionary fields as nullable so omitting them in a partial-update call preserves
/// the existing stored values instead of blanking them to defaults.
/// </summary>
public class FactionUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public FactionType FactionType { get; set; } = FactionType.Guild;

    public string? ControllingTerritory { get; set; }

    [Description("Omit to preserve the faction's existing territory location IDs. Provide to replace them wholesale.")]
    public List<string>? TerritoryLocationIds { get; set; }

    [Description("Omit to preserve the faction's existing known leader IDs. Provide to replace them wholesale.")]
    public List<string>? KnownLeaderIds { get; set; }

    [Description("Influence level (0-100). Defaults to 50 for a new faction; omit on update to preserve the existing value.")]
    public int? InfluenceLevel { get; set; }

    [Description("Set true to hide this faction from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_quest. Mirrors <see cref="Quest"/>, but declares
/// list fields as nullable so omitting them in a partial-update call preserves
/// the existing stored values instead of blanking them to defaults.
/// </summary>
public class QuestUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? GiverId { get; set; }

    [Description("Omit to preserve the quest's existing objectives. Provide to replace them wholesale.")]
    public List<QuestObjective>? Objectives { get; set; }

    public string? Category { get; set; }

    public QuestUrgency Urgency { get; set; } = QuestUrgency.Normal;

    [Description("Omit to preserve the quest's existing related location IDs. Provide to replace them wholesale.")]
    public List<string>? RelatedLocationIds { get; set; }

    [Description("Omit to preserve the quest's existing related faction IDs. Provide to replace them wholesale.")]
    public List<string>? RelatedFactionIds { get; set; }

    public string? DmNotes { get; set; }

    public int? DeadlineDay { get; set; }

    [Description("Set true to hide this quest from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_spell. Mirrors <see cref="CustomSpell"/>, but declares
/// Classes as nullable so omitting it in a partial-update call preserves the existing stored value.
/// </summary>
public class CustomSpellUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string System { get; set; }

    public string? Description { get; set; }

    public int? Level { get; set; }

    [Description("Omit to preserve the spell's existing class list. Provide to replace it wholesale.")]
    public List<string>? Classes { get; set; }

    public bool? Concentration { get; set; }

    public string? CastingTime { get; set; }

    [Description("Set true to hide this spell from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_feat. Mirrors <see cref="CustomFeat"/>.
/// </summary>
public class CustomFeatUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string System { get; set; }

    public string? Description { get; set; }

    public string? Prerequisite { get; set; }

    public string? MechanicalSummary { get; set; }

    [Description("Set true to hide this feat from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_rumor. Mirrors <see cref="Rumor"/>.
/// </summary>
public class RumorUpsertRequest
{
    public string Id { get; set; } = null!;

    public string? RegionLocationId { get; set; }

    public string Subject { get; set; } = null!;

    public string CurrentText { get; set; } = null!;

    public RumorState State { get; set; } = RumorState.Nascent;

    public RumorTruth TruthValue { get; set; } = RumorTruth.True;

    [Description("Set true to hide this rumor from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_world_event. Mirrors <see cref="WorldEvent"/>, but declares
/// collection fields as nullable so omitting them in a partial-update call preserves the
/// existing stored values instead of blanking them to defaults.
/// </summary>
public class WorldEventUpsertRequest
{
    public string Id { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public string? ActorId { get; set; }

    [Description("Omit to preserve the event's existing involved entity IDs. Provide to replace them wholesale.")]
    public List<string>? InvolvedEntityIds { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorldEventTriggerType TriggerType { get; set; } = WorldEventTriggerType.Scheduled;

    [Description("For TimeBased events: fire every N days (recurring).")]
    public int? IntervalDays { get; set; }

    [Description("For Scheduled events: fire once when TotalDaysElapsed >= this value.")]
    public int? TargetDay { get; set; }

    [Description("For Conditional events: fire when this condition is satisfied (Phase 2).")]
    public WorldEventCondition? Condition { get; set; }

    [Description("Omit to preserve the event's existing effects. Provide to replace them wholesale.")]
    public List<WorldEventEffect>? Effects { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorldEventStatus Status { get; set; } = WorldEventStatus.Pending;

    public bool IsPlayerVisible { get; set; }

    public string? DmNotes { get; set; }

    [Description("Set true to hide this event from default search/scene results (soft delete). Omit to preserve the existing value on update.")]
    public bool? IsArchived { get; set; }

    public string? CampaignName { get; set; }
}
