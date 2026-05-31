namespace CampaignVault.Data;

/// <summary>
/// The simulation engine orchestrates one or more ISimulationRule implementations
/// during AdvanceWorld calls.
/// 
/// This is the primary extensibility seam. Different implementations can be swapped
/// (e.g. the default rule-based engine today, a future DefaultEcs-backed engine, or a hybrid).
/// </summary>
public interface IWorldSimulationEngine
{
    Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default);
}
