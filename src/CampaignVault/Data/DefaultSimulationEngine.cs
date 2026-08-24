using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Default implementation of IWorldSimulationEngine.
/// 
/// Composes an ordered list of ISimulationRule instances (injected via DI).
/// Rules are executed sequentially; their emitted deltas and narratives are aggregated.
/// 
/// This is intentionally simple and extensible. A future DefaultEcs-based engine
/// could implement the same interface and be swapped in DI with no changes to the repository.
/// </summary>
public sealed class DefaultSimulationEngine : IWorldSimulationEngine
{

    private readonly IReadOnlyList<ISimulationRule> _rules;
    private readonly ILogger<DefaultSimulationEngine> _logger;

    public DefaultSimulationEngine(
        IEnumerable<ISimulationRule> rules,
        ILogger<DefaultSimulationEngine>? logger = null)
    {
        _rules = rules.OrderBy(r => r.Order).ToList();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultSimulationEngine>.Instance;
    }

    public async Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default)
    {
        var allNarratives = new List<RuleNarrative>();
        var allDeltas = new List<WorldChange>();
        var pressure = new List<WorldPressureItem>();
        var allEvictedIds = new List<string>();
        var allEvictedSummaries = new List<EvictedNpcSummary>();

        _logger.LogInformation("Running simulation engine with {RuleCount} rules for {Days} days", _rules.Count, context.DaysPassed);

        foreach (var rule in _rules)
        {
            RuleResult result;
            try
            {
                result = await rule.ApplyAsync(context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulation rule {RuleName} failed; continuing with remaining rules", rule.Name);
                allNarratives.Add(new RuleNarrative($"[Engine] Rule '{rule.Name}' failed and was skipped this tick: {ex.Message}", Persist: true));
                continue;
            }

            if (result.Narratives.Count > 0)
            {
                allNarratives.AddRange(result.Narratives);
                _logger.LogDebug("Rule {RuleName} produced {Count} narrative events", rule.Name, result.Narratives.Count);
            }

            if (result.Deltas.Count > 0)
            {
                // Mark all engine-produced deltas so handlers can distinguish them from LLM commits
                foreach (var delta in result.Deltas)
                {
                    delta.IsEngineAuthored = true;
                }
                allDeltas.AddRange(result.Deltas);
                _logger.LogDebug("Rule {RuleName} produced {Count} deltas", rule.Name, result.Deltas.Count);
            }

            if (result.EvictedEntityIds is { Count: > 0 })
                allEvictedIds.AddRange(result.EvictedEntityIds);

            if (result.EvictedNpcSummaries is { Count: > 0 })
                allEvictedSummaries.AddRange(result.EvictedNpcSummaries);
        }

        // Basic pressure signals (can be expanded by dedicated rules later)
        if (context.ActiveRumors.Any(r => r.State is RumorState.Peak or RumorState.Spreading))
        {
            pressure.Add(new WorldPressureItem(PressureSeverity.Simulation, "Simulation", "Active rumors are circulating and may require attention.", WorldPressureItem.RumorsGroupingKey));
        }

        return new SimulationResult(
            allNarratives.AsReadOnly(),
            allDeltas.AsReadOnly(),
            pressure.AsReadOnly(),
            allEvictedIds.AsReadOnly(),
            allEvictedSummaries.AsReadOnly()
        );
    }
}
