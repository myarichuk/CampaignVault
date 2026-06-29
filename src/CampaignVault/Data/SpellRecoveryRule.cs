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

            if (character.LastRestType == null)
            {
                continue;
            }

            foreach (var (poolName, pool) in character.SystemStats.ResourcePools)
            {
                // Skip if pool is already at max
                if (pool.Current == pool.Max)
                {
                    continue;
                }

                // Check if this pool should recover based on rest type hierarchy
                if (!ShouldRecoverPool(character.LastRestType.Value, pool.Recovery))
                {
                    continue;
                }

                var recovery = pool.Max - pool.Current;
                deltas.Add(new ResourceChange
                {
                    CharacterId = character.Id,
                    PoolName = poolName,
                    Delta = recovery,
                    Reason = $"{character.LastRestType} rest recovery"
                });

                narratives.Add($"{character.Name} recovered {recovery} {poolName} after {character.LastRestType} rest.");
                _logger.LogDebug("SpellRecoveryRule: {CharacterName} recovered {Count} {PoolName} after {RestType} rest",
                    character.Name, recovery, poolName, character.LastRestType);
            }
        }

        return new RuleResult(narratives, deltas);
    }

    /// <summary>
    /// Recovery type hierarchy matrix: determines which resource pools recover for a given rest type.
    /// LongRest ⊃ ShortRest ⊃ PerTurn (each includes lower levels).
    /// </summary>
    private static bool ShouldRecoverPool(RestType restTaken, RecoveryType poolRecovery) => (restTaken, poolRecovery) switch
    {
        // LongRest recovers everything: LongRest pools, ShortRest pools, PerTurn pools
        (RestType.LongRest, RecoveryType.LongRest) => true,
        (RestType.LongRest, RecoveryType.ShortRest) => true,
        (RestType.LongRest, RecoveryType.PerTurn) => true,

        // ShortRest recovers ShortRest and PerTurn pools (not LongRest)
        (RestType.ShortRest, RecoveryType.ShortRest) => true,
        (RestType.ShortRest, RecoveryType.PerTurn) => true,

        // PerTurn only recovers PerTurn pools
        (RestType.PerTurn, RecoveryType.PerTurn) => true,

        // All other combinations don't recover (Daily, Never, EncounterEnd are independent)
        _ => false
    };
}
