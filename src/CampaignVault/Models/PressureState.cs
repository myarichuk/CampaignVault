namespace CampaignVault.Models;

/// <summary>
/// Tracks the state of an active pressure nag for deduplication and escalation.
/// </summary>
public record PressureState(int LastSurfacedDay, int SuppressionCount);
