using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class CombatantState
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;

    [JsonPropertyName("initiative")]
    public float Initiative { get; set; }

    [JsonPropertyName("hasActedThisRound")]
    public bool HasActedThisRound { get; set; }

    [JsonPropertyName("actionBudget")]
    public Dictionary<string, int> ActionBudget { get; set; } = [];

    [JsonPropertyName("reactionAvailable")]
    public bool ReactionAvailable { get; set; } = true;
}

public class CombatEncounter
{
    /// <summary>
    /// Document ID. Should be provided by CampaignDocumentKeys.CombatCurrent(campaignName)
    /// (e.g. "campaigns/{name}/combat/current").
    /// The old hardcoded "combat/current" singleton is deprecated in favor of per-campaign namespacing.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("locationId")]
    public string LocationId { get; set; } = default!;

    [JsonPropertyName("round")]
    public int Round { get; set; } = 1;

    [JsonPropertyName("combatants")]
    public List<CombatantState> Combatants { get; set; } = [];

    [JsonPropertyName("activeTurnId")]
    public string? ActiveTurnId { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
