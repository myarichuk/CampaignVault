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

        // We check all characters since PCs and monsters might have status effects that expire over time.
        var allCharacters = await context.Session.Query<Character>().ToListAsync(ct);

        foreach (var character in allCharacters)
        {
            if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
                continue;

            var expiredEffects = character.SystemStats.StatusEffects
                .Where(e => (e.ExpiresAtDay.HasValue && e.ExpiresAtDay.Value <= context.Time.TotalDaysElapsed) ||
                            (e.ExpiresAtRound.HasValue && context.Time.TotalDaysElapsed > 0)) // If days elapsed, definitely remove round-based effects.
                .ToList();

            foreach (var effect in expiredEffects)
            {
                deltas.Add(new StatusRemove
                {
                    CharacterId = character.Id,
                    Status = effect.Name
                });
                narratives.Add($"Expired effect '{effect.Name}' on '{character.Name}' due to time passing.");
            }
        }

        return new RuleResult(narratives, deltas);
    }
}
