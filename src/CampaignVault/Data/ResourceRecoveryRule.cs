using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Recovers resource pools (spell slots, focus points, etc.) after long or short rests.
/// Rest-based recovery now happens synchronously inside the "rest" commit itself
/// (see <see cref="RestRecoveryLogic"/>, called from RestChangeHandler); this rule's rest-based
/// pass is a defense-in-depth fallback for advance_world and is normally a no-op, since
/// RestRecoveryLogic.IsRestAlreadyRecovered guards both call sites identically. The separate
/// Daily-recovery pass, gated purely by ResourcePool.LastRecoveredDay, is unrelated to resting
/// and always runs here only.
///
/// LIMITATION: PerTurn recovery is not automatically handled.
/// The LLM must manually reset these via resource commits at the start of each turn in combat.
/// Example: commit { $type: "resource", characterId: "chars/agent-1", poolName: "action_points", delta: 10, reason: "Turn start" }
/// </summary>
public class ResourceRecoveryRule : ISimulationRule
{
    private readonly ILogger<ResourceRecoveryRule> _logger;

    public string Name => "Resource Pool Recovery";
    public int Order => 38; // After NeedsAccumulation (35) and ScheduleEvaluation (30)

    public ResourceRecoveryRule(ILogger<ResourceRecoveryRule> logger)
    {
        _logger = logger;
    }

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        try
        {
            var narratives = new List<string>();
            var deltas = new List<WorldChange>();
            var currentDay = context.Time.TotalDaysElapsed;

            foreach (var character in context.ScheduledNpcs)
            {
                if (character.SystemStats?.ResourcePools == null || character.SystemStats.ResourcePools.Count == 0)
                {
                    continue;
                }

                ApplyDailyRecovery(character, currentDay, narratives, deltas);

                deltas.AddRange(RestRecoveryLogic.BuildRecoveryDeltas(character, narratives, _logger));
            }

            return Task.FromResult(new RuleResult(narratives, deltas));
        }
        catch (Exception exception)
        {
            return Task.FromException<RuleResult>(exception);
        }
    }

    /// <summary>
    /// Daily recovery is independent of rest state (see ShouldRecoverPool) — gated only by
    /// ResourcePool.LastRecoveredDay so it fires at most once per campaign day.
    /// </summary>
    private void ApplyDailyRecovery(Character character, int currentDay, List<string> narratives, List<WorldChange> deltas)
    {
        foreach (var (poolName, pool) in character.SystemStats!.ResourcePools!)
        {
            if (pool.Recovery != RecoveryType.Daily)
            {
                continue;
            }

            if (pool.Current == pool.Max)
            {
                continue;
            }

            if (pool.LastRecoveredDay is { } lastRecoveredDay && lastRecoveredDay >= currentDay)
            {
                continue;
            }

            var recovery = pool.Max - pool.Current;
            deltas.Add(new ResourceChange
            {
                CharacterId = character.Id,
                PoolName = poolName,
                Delta = recovery,
                RecoveredOnDay = currentDay,
                Reason = "Daily recovery"
            });

            narratives.Add($"{character.Name} recovered {recovery} {poolName} (daily recovery).");
            _logger.LogDebug("ResourceRecoveryRule: {CharacterName} recovered {Count} {PoolName} (daily)",
                character.Name, recovery, poolName);
        }
    }

}
