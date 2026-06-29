using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Snapshot of a transient NPC who left a location via engine eviction.
/// Stored on <see cref="Location.RecentlyDeparted"/> for scene recall without event archaeology.
/// </summary>
public record DepartedNpcRecord(
    [property: JsonPropertyName("characterId")] string CharacterId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("departedAtDay")] int DepartedAtDay,
    [property: JsonPropertyName("reason")] string? Reason = null)
{
    public DepartedNpcRecord() : this(default!, default!, default) { }
}