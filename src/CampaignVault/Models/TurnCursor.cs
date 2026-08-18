using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Per-campaign singleton tracking take_turn's Full/Delta reseed cadence. Document ID should be
/// provided by CampaignDocumentKeys.StateTurnCursor(campaignName) (e.g. "campaigns/{name}/state/turn-cursor").
/// Absence of this document means "no take_turn call has happened yet" — the first call is naturally Full.
/// </summary>
public class TurnCursor
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("campaignName")]
    public string? CampaignName { get; set; }

    /// <summary>Number of take_turn calls (mutation or pure-query) since the last Full response. Reset to 0 on Full.</summary>
    [JsonPropertyName("turnsSinceReseed")]
    public int TurnsSinceReseed { get; set; }

    [JsonPropertyName("lastFullReseedUtc")]
    public DateTime? LastFullReseedUtc { get; set; }

    /// <summary>
    /// Set by advance_world (which can run simulation ticks outside the take_turn pipeline) to force
    /// the next take_turn call to Full regardless of TurnsSinceReseed, so drift from a skip isn't missed.
    /// </summary>
    [JsonPropertyName("forcedFullReseedPending")]
    public bool ForcedFullReseedPending { get; set; }
}
