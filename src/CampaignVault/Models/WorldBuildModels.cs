namespace CampaignVault.Models;

using System.ComponentModel;

/// <summary>
/// Batch payload for the <c>world_build</c> tool — struct-of-typed-arrays, one array per entity
/// kind. Every array is optional; include only the kinds you're seeding in this call. Dispatched
/// in a fixed dependency order (locations → factions → creatures/spells/feats → characters →
/// items → quests → plotThreads → lore → rumors → needDescriptors) inside a single session/save,
/// so a hard failure on any one entry rolls back the entire batch.
/// </summary>
public class WorldBuildBatch
{
    [Description("Locations to create or update. Dispatched first (parentLocationId/exits target other locations in this same array).")]
    public List<LocationUpsertRequest>? Locations { get; set; }

    [Description("Factions to create or update. Dispatched after locations (territoryLocationIds may reference them).")]
    public List<FactionUpsertRequest>? Factions { get; set; }

    [Description("Homebrew creature stat-block templates to create or update.")]
    public List<CustomCreatureUpsertRequest>? Creatures { get; set; }

    [Description("Homebrew spells to create or update.")]
    public List<CustomSpellUpsertRequest>? Spells { get; set; }

    [Description("Homebrew feats/perks to create or update.")]
    public List<CustomFeatUpsertRequest>? Feats { get; set; }

    [Description("Characters/NPCs to create or update. Dispatched after locations/factions (currentLocationId may reference them). Bootstrap (HP/defense derivation) runs per element.")]
    public List<CharacterUpsertRequest>? Characters { get; set; }

    [Description("Items to create or update. Dispatched after characters (holderId may reference a character just created in this batch).")]
    public List<ItemUpsertRequest>? Items { get; set; }

    [Description("Quests to create or update. Dispatched after characters/locations/factions (giverId/relatedLocationIds/relatedFactionIds may reference them).")]
    public List<QuestUpsertRequest>? Quests { get; set; }

    [Description("Plot threads to create or update. Dispatched after characters/locations/factions/quests (involvedEntityIds may reference them).")]
    public List<PlotThreadUpsertRequest>? PlotThreads { get; set; }

    [Description("Lore entries to create or update.")]
    public List<LoreUpsertRequest>? Lore { get; set; }

    [Description("Rumors to create or update. Dispatched last among entities (regionLocationId references a location).")]
    public List<RumorUpsertRequest>? Rumors { get; set; }

    [Description("Optional need-descriptor definitions (needName -> human-readable explanation), merged in after all entities.")]
    public Dictionary<string, string>? NeedDescriptors { get; set; }
}

/// <summary>Per-kind created/updated counts and IDs for a <c>world_build</c> call.</summary>
public class WorldBuildKindResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public List<string> CreatedIds { get; set; } = [];
}

/// <summary>Aggregate result of a <c>world_build</c> batch — per-kind counts plus accumulated warnings.</summary>
public class WorldBuildResult
{
    public Dictionary<string, WorldBuildKindResult> Kinds { get; set; } = [];

    [Description("Non-blocking warnings: dangling forward references, ID prefix normalizations, bootstrap hints/notes.")]
    public List<string> Warnings { get; set; } = [];

    public int NeedDescriptorsSet { get; set; }
}
