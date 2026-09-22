using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Generalized <see cref="CombatantState"/> for a non-combat interaction mode (crafting, hairstyling,
/// astral combat, ...). See PLUGIN_SYSTEM_PLAN.md Track B / INTERACTION_MODES_PLAN.md.
/// </summary>
public class ModeParticipantState
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = null!;

    /// <summary>Turn-scoped resource counters (mirrors CombatantState.ActionBudget's shape).</summary>
    [JsonPropertyName("actionBudget")]
    public Dictionary<string, int> ActionBudget { get; set; } = [];

    /// <summary>
    /// Mode-owned scratch state (e.g. "stage": "cut" for a hairstyling mode). Opaque to the engine —
    /// only the owning IInteractionMode's WorldChange handlers read/write these keys.
    /// </summary>
    [JsonPropertyName("state")]
    public Dictionary<string, object> State { get; set; } = [];
}

/// <summary>
/// Generalized <see cref="CombatEncounter"/> for a non-combat interaction mode. One per (campaign, modeId)
/// at a time, keyed via CampaignDocumentKeys.ModeCurrent.
/// </summary>
public class ModeEncounter
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("modeId")]
    public string ModeId { get; set; } = null!;

    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = null!;

    [JsonPropertyName("round")]
    public int Round { get; set; } = 1;

    [JsonPropertyName("participants")]
    public List<ModeParticipantState> Participants { get; set; } = [];

    [JsonPropertyName("activeTurnId")]
    public string? ActiveTurnId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
