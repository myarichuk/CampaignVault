namespace CampaignVault.Data;

/// <summary>
/// A single, focused, composable unit of world simulation.
/// 
/// Rules are the plugin/extension point. New behaviors (schedule evaluation, agency/initiative,
/// faction pressure, weather, complex NPC psychology, etc.) are added by implementing this interface
/// and registering the rule in DI.
/// 
/// Preferred contract: Rules emit WorldChange deltas (which are applied through the existing
/// StageChangesAsync / Commit machinery in CampaignRepository) + narrative strings for the DM.
/// Rules should avoid direct mutation of entities when possible.
/// </summary>
public interface ISimulationRule
{
    /// <summary>
    /// Human-readable name for logging and diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Execution order of the simulation rule. Lower values run first.
    /// </summary>
    int Order { get; }

    Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default);
}
