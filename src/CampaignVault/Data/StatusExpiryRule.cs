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

        // POLICY NOTE: Entities (like Character) are not currently namespaced per-campaign.
        // Therefore, this rule queries globally across all campaigns. Singletons (like CampaignConfig)
        // provide the isolation boundary.
        var allCharacters = await context.Session.Query<Character>().ToListAsync(ct);

        foreach (var character in allCharacters)
        {
            if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
                continue;

            // This rule is responsible exclusively for day-based expiry.
            // Round-based expiry is handled natively inside combat tools (e.g. NextTurn, EndCombat).
            var expiredEffects = character.SystemStats.StatusEffects
                .Where(e => e.ExpiresAtDay.HasValue && e.ExpiresAtDay.Value <= context.Time.TotalDaysElapsed)
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
