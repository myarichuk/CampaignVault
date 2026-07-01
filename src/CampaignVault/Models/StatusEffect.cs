using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// A structured status condition on a character, authored entirely by the LLM DM.
/// Replaces the old <c>Character.Status: List&lt;string&gt;</c>.
///
/// DESIGN: The MCP is the clock and the store. The LLM DM is the sole decision-maker
/// on what modifiers apply, whether the effect expires automatically, and when to remove it.
///
/// EXPIRATION:
/// - <see cref="ExpiresAtDay"/>: set to <c>CampaignTime.TotalDaysElapsed + N</c> for wounds
///   that heal over time. AdvanceWorldAsync will auto-remove expired effects.
/// - <see cref="ExpiresAtRound"/>: set to <c>CombatEncounter.Round + N</c> for short-duration
///   combat conditions (e.g. Frightened 2 → currentRound + 2).
/// - Leave both null for permanent effects (broken bones, curses) that only leave via
///   an explicit StatusRemove call.
///
/// RECOVERY: The LLM reads <see cref="RecoveryHint"/> when deciding whether a medicine check
/// or item use qualifies to remove this effect. It then calls StatusRemove directly.
/// The MCP does NOT enforce RecoveryHint — it is a sticky-note for the LLM.
/// </summary>
public class StatusEffect
{
    /// <summary>
    /// Human-readable name the LLM DM chooses.
    /// Examples: "Mangled Left Hand", "Frightened 2", "Pinned Foot (arrow)", "Broken Rib".
    /// Used as the key when calling StatusRemove.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    /// <summary>
    /// Broad category for filtering and display. Suggested values:
    /// "Injury", "Condition", "Buff", "Disease", "Poison", "Curse", "Environmental".
    /// The LLM may invent categories — these are hints, not an enforced enum.
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = default!;

    /// <summary>
    /// Which body part is affected. Null means whole-body / systemic.
    /// </summary>
    [JsonPropertyName("affectedPart")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BodyPart? AffectedPart { get; set; }

    /// <summary>
    /// Stat modifiers the LLM DM decides are appropriate for the fiction.
    /// Keys are free-form strings the resolver reads when calculating rolls.
    /// Suggested canonical keys: "AttackRoll", "AllChecks", "AllRolls", "DC",
    /// "Speed", "Initiative", "LeftArmAttacks", "RightArmAttacks",
    /// "Athletics", "Perception", "Stealth".
    /// Positive = bonus, negative = penalty.
    /// </summary>
    [JsonPropertyName("statModifiers")]
    public Dictionary<string, float> StatModifiers { get; set; } = [];

    /// <summary>
    /// Campaign-day timestamp for auto-expiry (uses <c>CampaignTime.TotalDaysElapsed</c>).
    /// Set to <c>currentDay + N</c> for wounds that heal with rest.
    /// Leave null if the effect must NOT auto-expire by passage of time.
    /// </summary>
    [JsonPropertyName("expiresAtDay")]
    public float? ExpiresAtDay { get; set; }

    /// <summary>
    /// Combat-round number for auto-expiry (uses <c>CombatEncounter.Round</c>).
    /// Set to <c>currentRound + N</c> for short-duration conditions (e.g. Frightened 2).
    /// Leave null outside of combat or for effects not tied to round duration.
    /// </summary>
    [JsonPropertyName("expiresAtRound")]
    public int? ExpiresAtRound { get; set; }

    /// <summary>
    /// Free-text hint the LLM DM writes for its own future reference.
    /// NOT mechanically enforced by the MCP.
    /// Example: "Requires Medicine DC 15 check or magical healing.
    ///           Cannot be removed by mundane means if the bone is shattered."
    /// </summary>
    [JsonPropertyName("recoveryHint")]
    public string? RecoveryHint { get; set; }

    /// <summary>
    /// Who applied this effect. Used for audit trail and context.
    /// Typical values: "npcs/healer-id", "system/combat-resolver", "llm-dm".
    /// </summary>
    [JsonPropertyName("appliedBy")]
    public string? AppliedBy { get; set; }

    /// <summary>
    /// Optional reference to a <c>ConditionDefinition</c> template name (e.g. "frightened", "blinded").
    /// When set, <c>StatusExpiryRule</c> uses the definition's <c>DurationType</c> to gate
    /// day-based expiry rather than relying on <c>ExpiresAtDay</c> alone.
    /// Leave null for narrative / non-SRD effects.
    /// </summary>
    [System.ComponentModel.Description(
        "Optional SRD condition template key (e.g. \"frightened\", \"blinded\", \"exhaustion\"). " +
        "References a ConditionDefinition in RulesetData. Drives expiry: Timed uses expiresAtDay; " +
        "UntilLongRest clears on long rest; UntilDawn clears on advance_world; Manual/Concentration do not auto-expire by day. " +
        "Omit for narrative-only effects (wounds, mood, environmental states).")]
    [JsonPropertyName("conditionName")]
    public string? ConditionName { get; set; }
}
