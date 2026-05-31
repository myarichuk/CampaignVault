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
}

public class CombatEncounter
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "combat/current";

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
