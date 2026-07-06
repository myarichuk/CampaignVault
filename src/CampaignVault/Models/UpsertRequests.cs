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
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string? ClassLevel { get; set; }

    public int CurrentHp { get; set; }

    public int MaxHp { get; set; }

    public string? Notes { get; set; }

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
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string Description { get; set; } = default!;

    public LocationType Type { get; set; } = LocationType.Building;

    public string? ParentLocationId { get; set; }

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

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_lore. Mirrors <see cref="Lore"/>, but declares
/// Tags/Keywords as nullable so omitting them in a partial-update call preserves the
/// existing stored values instead of blanking them to defaults.
/// </summary>
public class LoreUpsertRequest
{
    public string Id { get; set; } = default!;

    public string Title { get; set; } = default!;

    public string Content { get; set; } = default!;

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
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    public string Description { get; set; } = default!;

    public string HolderId { get; set; } = default!;

    public int Quantity { get; set; } = 1;

    public string? CurrentState { get; set; }

    [Description("Omit to preserve the item's existing distinctive features. Provide to replace them wholesale.")]
    public List<string>? DistinctiveFeatures { get; set; }

    public ItemCategory CoreCategory { get; set; }

    [Description("Omit to preserve the item's existing tags. Provide to replace them wholesale.")]
    public List<string>? Tags { get; set; }

    [Description("Omit to preserve the item's existing properties. Provide to replace them wholesale.")]
    public Dictionary<string, object>? Properties { get; set; }

    public string? CampaignName { get; set; }
}

/// <summary>
/// Tool-facing request for upsert_plot_thread. Mirrors <see cref="PlotThread"/>, but declares
/// Clues/ForeshadowingHooks/InvolvedEntityIds as nullable so omitting them in a partial-update call
/// (e.g. bumping TensionLevel) preserves the existing stored values instead of blanking them.
/// </summary>
public class PlotThreadUpsertRequest
{
    public string Id { get; set; } = default!;

    public string Title { get; set; } = default!;

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

    public string? CampaignName { get; set; }
}
