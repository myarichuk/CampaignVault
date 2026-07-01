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

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        foreach (var character in context.ScheduledNpcs)
        {
            if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
                continue;

            // Day-based expiry (Timed + legacy free-text effects with ExpiresAtDay).
            var dayExpired = character.SystemStats.StatusEffects
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

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}