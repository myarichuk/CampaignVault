using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Aggregated result from running the full simulation engine for one AdvanceWorld tick.
/// </summary>
public sealed record SimulationResult(
    IReadOnlyList<string> NarrativeEvents,
    IReadOnlyList<WorldChange> Deltas,
    IReadOnlyList<WorldPressureItem> WorldPressure,
    IReadOnlyList<string> EvictedNpcIds,
    IReadOnlyList<EvictedNpcSummary> EvictedNpcSummaries
);
