using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class ResolverResult
{
    public string Narrative { get; init; } = string.Empty;
}

public class ResolverOutput
{
    public IReadOnlyList<WorldChange> Mutations { get; init; } = Array.Empty<WorldChange>();
    public ResolverResult Result { get; init; } = new();
}

public interface IRulesetResolver
{
    RulesetSystem System { get; }
    
    Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default);

    Task<float> RollInitiativeAsync(
        IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default);
}
