using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : IRulesetResolver
{
    private readonly IRollService _rollService;

    public Dnd5eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public RulesetSystem System => RulesetSystem.Dnd5e;

    public Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        // Phase 2 stub. Full resolution math will be built in Phase 3.
        var output = new ResolverOutput
        {
            Mutations = Array.Empty<WorldChange>(),
            Result = new ResolverResult { Narrative = $"Resolved action '{action.ActionName}' via Dnd5e." }
        };
        return Task.FromResult(output);
    }

    public async Task<float> RollInitiativeAsync(
        IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null) return 0f;

        // Basic Phase 2 initiative (ignoring character stats for the stub).
        var request = new RollRequest 
        { 
            Tag = "initiative", 
            Expression = "1d20", 
            Mechanic = DiceMechanic.Standard 
        };
        
        var outcome = await _rollService.RollAsync(request, ct);
        return outcome.Result;
    }
}
