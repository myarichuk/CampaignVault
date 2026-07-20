namespace CampaignVault.Models;

/// <summary>
/// Tracks the state of an active pressure nag for deduplication and escalation.
/// </summary>
public record PressureState(int LastSurfacedDay, int SuppressionCount)
{
    public PressureState() : this(default!, default!) { }

    /// <summary>
    /// Content signature (see PressureHelpers.ComputeContentSignature) of the Text last surfaced under
    /// this cooldown key. Null on entries predating this field (no migration needed — treated as "no
    /// prior signature to compare," so old cooldown state isn't force-reset for unchanged text).
    /// </summary>
    public string? LastSignature { get; init; }
}
