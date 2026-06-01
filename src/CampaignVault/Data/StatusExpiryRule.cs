using CampaignVault.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public sealed class StatusExpiryRule : ISimulationRule
{
    public string Name => "Status Expiry Rule";
    
    public int Order => 5; // Runs early before needs and routines

    public async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // Campaign-aware query preparation (entities are still primarily ID-controlled).
        // CampaignName is available in context for future entity-level namespacing.
        var query = context.Session.Query<Character>();
        var allCharacters = await query.ToListAsync(ct);

        foreach (var character in allCharacters)
        {
            if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
                continue;

            var expiredEffects = character.SystemStats.StatusEffects
                .Where(e =>
                    (e.ExpiresAtDay.HasValue && e.ExpiresAtDay.Value <= context.Time.TotalDaysElapsed) ||
                    (e.ExpiresAtRound.HasValue && context.CurrentRound >= e.ExpiresAtRound.Value))
                .ToList();

            foreach (var effect in expiredEffects)
            {
                deltas.Add(new StatusRemove
                {
                    CharacterId = character.Id,
                    Status = effect.Name
                });
                narratives.Add($"Expired effect '{effect.Name}' on '{character.Name}' (round/day based).");
            }
        }

        return new RuleResult(narratives, deltas);
    }
}
