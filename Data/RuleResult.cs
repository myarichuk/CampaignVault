using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Result returned by a single ISimulationRule.
/// Rules should prefer emitting WorldChange deltas (so they go through the unified Commit path)
/// rather than mutating entities directly.
/// </summary>
public sealed record RuleResult(
    IReadOnlyList<string> NarrativeEvents,
    IReadOnlyList<WorldChange> Deltas
);
