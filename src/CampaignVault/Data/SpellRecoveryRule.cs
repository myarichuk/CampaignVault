using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Recovers resource pools (spell slots, focus points, etc.) after long or short rests.
/// Runs daily during advance_world and checks each character's LastRestedDay.
/// If a rest was completed on the current or previous day, refills matching pools.
///
/// LIMITATION: PerTurn recovery (Fallout 2d20 Action Points) is not automatically handled.
/// The LLM must manually reset these via resource commits at the start of each turn in combat.
/// Example: commit { $type: "resource", characterId: "chars/agent-1", poolName: "action_points", delta: 10, reason: "Turn start" }
/// </summary>
public class SpellRecoveryRule : ISimulationRule
{
    private readonly ILogger<SpellRecoveryRule> _logger;

    public string Name => "Spell Slot & Resource Recovery";
    public int Order => 38; // After ScheduleEvaluation (35), before NeedsAccumulation (35)

    public SpellRecoveryRule(ILogger<SpellRecoveryRule> logger)
    {
        _logger = logger;
    }

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
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

            // Check if this character rested recently (within the last day)
            if (character.LastRestedDay == null || currentDay - character.LastRestedDay.Value > 1)
            {
                continue;
            }

            var restedToday = character.LastRestedDay.Value == (int)currentDay;
            var restType = restedToday ? "long" : "recent";

            foreach (var (poolName, pool) in character.SystemStats.ResourcePools)
            {
                // Skip if pool is already at max or doesn't recover via rest
                if (pool.Current == pool.Max)
                {
                    continue;
                }

                // Skip PerTurn pools (manually managed by LLM) and other non-rest recovery types
                if (pool.Recovery == RecoveryType.PerTurn || pool.Recovery == RecoveryType.EncounterEnd || pool.Recovery == RecoveryType.Daily || pool.Recovery == RecoveryType.Never)
                {
                    continue;
                }

                // Only recover LongRest and ShortRest types
                if (pool.Recovery != RecoveryType.LongRest && pool.Recovery != RecoveryType.ShortRest)
                {
                    continue;
                }

                // Skip short-rest recovery if only a day has passed (recovery should happen when short rest is actually taken)
                // For now, treat both as long-rest for simplicity; LLM can emit multiple short rests if needed
                if (pool.Recovery == RecoveryType.ShortRest && !restedToday)
                {
                    continue;
                }

                var recovery = pool.Max - pool.Current;
                deltas.Add(new ResourceChange
                {
                    CharacterId = character.Id,
                    PoolName = poolName,
                    Delta = recovery,
                    Reason = $"{restType} rest recovery"
                });

                narratives.Add($"{character.Name} recovered {recovery} {poolName} after {restType} rest.");
                _logger.LogDebug("SpellRecoveryRule: {CharacterName} recovered {Count} {PoolName}", character.Name, recovery, poolName);
            }
        }

        return new RuleResult(narratives, deltas);
    }
}
