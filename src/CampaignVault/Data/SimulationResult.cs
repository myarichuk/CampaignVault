using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Aggregated result from running the full simulation engine for one AdvanceWorld tick.
/// </summary>
public sealed record SimulationResult(
    IReadOnlyList<RuleNarrative> Narratives,
    IReadOnlyList<WorldChange> Deltas,
    IReadOnlyList<WorldPressureItem> WorldPressure,
    IReadOnlyList<string> EvictedNpcIds,
    IReadOnlyList<EvictedNpcSummary> EvictedNpcSummaries
)
{
    // Back-compat property: extract text from all narratives (both persistent and ephemeral)
    public IReadOnlyList<string> NarrativeEvents => Narratives.Select(n => n.Text).ToList();
}
