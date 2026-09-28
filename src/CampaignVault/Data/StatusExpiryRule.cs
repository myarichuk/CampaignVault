using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data;

public class StatusExpiryRule : ISimulationRule
{
    private readonly ConditionDefinitionProvider _conditionProvider;

    public StatusExpiryRule(ConditionDefinitionProvider conditionProvider)
    {
        _conditionProvider = conditionProvider;
    }

    public string Name => "Status Expiry Rule";

    public int Order => 5; // Runs early before needs and routines

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        foreach (var character in context.ScheduledNpcs)
        {
            if (character.SystemStats?.StatusEffects is { Count: > 0 } effects)
            {
                // Day-based expiry (Timed + legacy free-text effects with ExpiresAtDay).
                var dayExpired = effects
                    .Where(e => ConditionExpiryEvaluator.ShouldExpireByElapsedDay(
                        e,
                        ConditionExpiryEvaluator.TryResolve(_conditionProvider, character.SystemStats, e.ConditionName),
                        context.Time.TotalDaysElapsed))
                    .ToList();

                // UntilDawn: clears when advance_world moves at least one day forward.
                var dawnExpired = ConditionExpiryEvaluator.CollectDawnExpirations(
                    character,
                    _conditionProvider,
                    context.DaysPassed);

                foreach (var effect in dayExpired.Concat(dawnExpired).DistinctBy(e => e.Name))
                {
                    deltas.Add(new StatusRemove
                    {
                        CharacterId = character.Id,
                        Status = effect.Name
                    });
                    narratives.Add($"Expired effect '{effect.Name}' on '{character.Name}' due to time passing.");
                }
            }

            // Day-scoped minion bindings on this character itself.
            if (character.MinionBinding is { ControlLapsed: false, ExpiresAtDay: { } ownExpiry }
                && ownExpiry <= context.Time.TotalDaysElapsed)
            {
                LapseMinionViaDeltas(character, deltas, narratives);
            }

            // Day-scoped bindings on this caster's minions (loaded from the session;
            // unscheduled minions of a scheduled caster are covered here too).
            if (character.ControlsMinionIds.Count > 0)
            {
                foreach (var minionId in character.ControlsMinionIds.ToList())
                {
                    var minion = await context.Session.LoadAsync<Character>(minionId, ct);
                    if (minion?.MinionBinding is not { ControlLapsed: false, ExpiresAtDay: { } minionExpiry })
                    {
                        continue;
                    }

                    if (minionExpiry <= context.Time.TotalDaysElapsed)
                    {
                        LapseMinionViaDeltas(minion, deltas, narratives);
                    }
                }
            }
        }

        return new RuleResult(narratives, deltas);
    }

    /// <summary>
    /// Day-scope lapse via dispatched deltas (this rule never mutates docs in place):
    /// the minion's live link is cleared through the standard update handler and the
    /// controller's list is pruned in the same batch.
    /// </summary>
    private static void LapseMinionViaDeltas(Character minion, List<WorldChange> deltas, List<string> narratives)
    {
        var controllerId = minion.ControlledById ?? minion.MinionBinding?.ControllerId;
        narratives.Add(MinionLapse.LapseNarrative(minion, "outlasts its binding (duration elapsed)."));
        deltas.Add(new CharacterUpdate { CharacterId = minion.Id, ClearMinionLink = true });
        if (!string.IsNullOrEmpty(controllerId))
        {
            deltas.Add(new CharacterUpdate
            {
                CharacterId = controllerId,
                ControlsMinionIdsRemove = [minion.Id],
            });
        }
    }
}