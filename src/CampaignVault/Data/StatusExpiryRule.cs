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

        // Scoping hardened: filter by camp (loose for chars)
        var effective = context.CampaignName;
        var all = await context.Session.Query<Character>().ToListAsync(ct);
        var allCharacters = string.IsNullOrEmpty(effective) ? all : all.Where(c => string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective).ToList();

        foreach (var character in allCharacters)
        {
            if (character.SystemStats?.StatusEffects == null || character.SystemStats.StatusEffects.Count == 0)
            {
                continue;
            }

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
