using System.Text.Json.Serialization;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// A single tagged dice roll request submitted by the LLM DM.
/// Multiple requests can be batched in one RollBatchAsync call.
/// The LLM uses the Tag field to match each outcome back to its intended roll.
/// </summary>
public class RollRequest
{
    /// <summary>
    /// Label returned verbatim in RollOutcome. Used by the LLM to identify this roll.
    /// Examples: "attack", "damage", "intimidate", "actorAthletics", "targetAthletics".
    /// </summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = null!;

    /// <summary>
    /// Standard dice expression: "1d20+5", "2d6", "3d8+2".
    /// For RollUnder: pool size like "2d20" or "3d20".
    /// For KeepHighest/KeepLowest: use with <see cref="Keep"/>.
    /// </summary>
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = null!;

    /// <summary>Rolling mechanic to apply. Defaults to Standard.</summary>
    [JsonPropertyName("mechanic")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DiceMechanic Mechanic { get; set; } = DiceMechanic.Standard;

    /// <summary>
    /// Additional flat modifier added on top of the expression result.
    /// Useful for situational bonuses the LLM knows about (e.g. flanking +2)
    /// without needing to rebuild the full expression string.
    /// </summary>
    [JsonPropertyName("bonus")]
    public int Bonus { get; set; } = 0;

    /// <summary>
    /// For RollUnder: the threshold each die must meet or beat.
    /// </summary>
    [JsonPropertyName("targetNumber")]
    public int? TargetNumber { get; set; }

    /// <summary>For KeepHighest/KeepLowest: how many dice to retain.</summary>
    [JsonPropertyName("keep")]
    public int? Keep { get; set; }
}

/// <summary>
/// The outcome of a single tagged dice roll.
/// Returned in the same order as the corresponding RollRequests.
/// </summary>
public class RollOutcome
{
    /// <summary>Verbatim copy of RollRequest.Tag — lets the LLM match outcomes to requests.</summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = null!;

    /// <summary>Final numeric result (total for Standard/Advantage/etc.).</summary>
    [JsonPropertyName("result")]
    public int Result { get; set; }

    /// <summary>Every individual die value rolled, before the Bonus modifier. Full chain for Explosive.</summary>
    [JsonPropertyName("individualDice")]
    public List<int> IndividualDice { get; set; } = [];

    /// <summary>True for RollUnder when the outcome constitutes a success.</summary>
    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; set; }

    /// <summary>True when a natural 20 (d20 systems) or max-face (Explosive) was rolled.</summary>
    [JsonPropertyName("hasCritical")]
    public bool HasCritical { get; set; }

    /// <summary>True when a natural 1 (d20 systems) was rolled.</summary>
    [JsonPropertyName("hasComplication")]
    public bool HasComplication { get; set; }

    /// <summary>
    /// Human-readable summary for the LLM.
    /// Example: "[15]+5 = 20 (Advantage: kept 20 over 8)".
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = null!;
}

/// <summary>
/// Dice rolling service. All randomness in CampaignVault flows through here so that:
/// (a) the LLM never generates numbers (it only calls the MCP),
/// (b) tests can inject a seeded/deterministic implementation.
/// </summary>
public interface IRollService
{
    /// <summary>
    /// Roll a single tagged request and return its outcome.
    /// </summary>
    Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default);

    /// <summary>
    /// Roll multiple tagged requests in a single call.
    /// Results are returned in the same order as the input requests.
    /// Use this when the LLM needs several dice values for the same narrative moment
    /// (e.g. attack + damage, contested check, heal roll + check roll in one batch).
    /// </summary>
    Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
        IEnumerable<RollRequest> requests,
        CancellationToken ct = default);

}
