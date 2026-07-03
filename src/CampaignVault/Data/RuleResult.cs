using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// A narrative event emitted by a rule, with a flag indicating whether it should persist to the campaign log.
/// </summary>
public sealed record RuleNarrative(string Text, bool Persist = true);

/// <summary>
/// Result returned by a single ISimulationRule.
/// Rules should prefer emitting WorldChange deltas (so they go through the unified Commit path)
/// rather than mutating entities directly.
/// </summary>
public sealed record RuleResult(
    IReadOnlyList<RuleNarrative> Narratives,
    IReadOnlyList<WorldChange> Deltas,
    IReadOnlyList<string>? EvictedEntityIds = null,
    IReadOnlyList<EvictedNpcSummary>? EvictedNpcSummaries = null
)
{
    // Back-compat constructor: accept string list and wrap as default-persist narratives
    public RuleResult(
        IReadOnlyList<string> legacyNarratives,
        IReadOnlyList<WorldChange> deltas,
        IReadOnlyList<string>? evictedEntityIds = null,
        IReadOnlyList<EvictedNpcSummary>? evictedNpcSummaries = null)
        : this(
            legacyNarratives.Select(n => new RuleNarrative(n, Persist: true)).ToList(),
            deltas,
            evictedEntityIds,
            evictedNpcSummaries)
    {
    }

    // Convenience property: extract text from all narratives (regardless of Persist flag)
    public IReadOnlyList<string> NarrativeEvents => Narratives.Select(n => n.Text).ToList();
}
