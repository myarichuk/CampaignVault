using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Pure, per-character resource-pool recovery logic shared by <see cref="RestChangeHandler"/>
/// (immediate recovery when a rest commit completes) and <see cref="ResourceRecoveryRule"/>
/// (advance_world fallback sweep — normally a no-op once recovery has already run synchronously,
/// via the same RestSequence idempotency guard).
/// </summary>
public static class RestRecoveryLogic
{
    public static bool IsRestAlreadyRecovered(Character character)
    {
        if (character.RestSequence.HasValue)
        {
            return character.LastRecoveredRestSequence == character.RestSequence;
        }

        // Legacy saves predating RestSequence: fall back to day-only idempotency.
        return character.LastRestRecoveredDay == character.LastRestedDay;
    }

    /// <summary>
    /// Recovery type hierarchy matrix: determines which resource pools recover for a given rest type.
    /// LongRest ⊃ ShortRest ⊃ PerTurn (each includes lower levels).
    /// </summary>
    public static bool ShouldRecoverPool(RestType restTaken, RecoveryType poolRecovery) => (restTaken, poolRecovery) switch
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

        // All other combinations don't recover (Daily is handled separately; EncounterEnd is unimplemented)
        _ => false
    };

    /// <summary>
    /// Builds the ResourceChange (+ trailing RestRecoveryAck) deltas for pools eligible to recover
    /// given the character's current LastRestType/LastRestedDay/RestSequence. Reads only
    /// <paramref name="character"/> — no other simulation state — so it is safe to call either from
    /// inside the advance_world sweep (per ScheduledNpc) or synchronously from RestChangeHandler for
    /// the single character that just rested. Returns an empty list if there are no pools, no rest has
    /// been recorded yet, or recovery for this rest has already been applied (idempotent).
    /// </summary>
    public static List<WorldChange> BuildRecoveryDeltas(Character character, List<string> narratives, ILogger? logger = null)
    {
        var deltas = new List<WorldChange>();

        if (character.SystemStats?.ResourcePools == null || character.SystemStats.ResourcePools.Count == 0)
        {
            return deltas;
        }

        if (character.LastRestedDay == null || character.LastRestType == null)
        {
            return deltas;
        }

        if (IsRestAlreadyRecovered(character))
        {
            return deltas;
        }

        foreach (var (poolName, pool) in character.SystemStats.ResourcePools)
        {
            if (pool.Current == pool.Max)
            {
                continue;
            }

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
                RecoveredOnDay = character.LastRestedDay.Value,
                Reason = $"{character.LastRestType} rest recovery"
            });

            narratives.Add($"{character.Name} recovered {recovery} {poolName} after {character.LastRestType} rest.");
            logger?.LogDebug("RestRecoveryLogic: {CharacterName} recovered {Count} {PoolName} after {RestType} rest",
                character.Name, recovery, poolName, character.LastRestType);
        }

        deltas.Add(new RestRecoveryAck
        {
            CharacterId = character.Id,
            RestDay = character.LastRestedDay.Value,
            RestSequence = character.RestSequence ?? character.LastRestedDay.Value
        });

        return deltas;
    }

    /// <summary>
    /// Settles tiredness toward <paramref name="baseline"/> after a completed rest — the same
    /// settle-toward-baseline shape <see cref="ScheduleNeedSatisfactionRule"/> uses for scheduled NPCs,
    /// applied here because rest is a PC/companion action and rarely comes with a Schedule.Routines
    /// entry for ScheduleNeedSatisfactionRule to match. LongRest settles fully to baseline; ShortRest
    /// only settles half that distance (a short rest shouldn't fully erase exhaustion). PerTurn rests
    /// don't recover tiredness at all.
    /// </summary>
    public static WorldChange? BuildTirednessRecoveryDelta(Character character, RestType restType, int baseline)
    {
        if (character.Needs == null || restType == RestType.PerTurn)
        {
            return null;
        }

        var current = character.Needs.ActiveNeeds.GetValueOrDefault("tiredness", 0f);
        var fullDelta = -(current - baseline);
        if (fullDelta >= -0.0001f)
        {
            return null;
        }

        var delta = restType == RestType.LongRest ? fullDelta : fullDelta / 2f;

        return new NeedChange
        {
            CharacterId = character.Id,
            Need = "tiredness",
            Delta = delta
        };
    }
}
