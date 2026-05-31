using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RulesetActionHandler : IWorldChangeHandler
{
    private readonly IRulesetResolverSelector _selector;
    private readonly CampaignDocumentKeys _keys;
    private readonly ICurrentCampaignContext _currentCampaign;

    public RulesetActionHandler(
        IRulesetResolverSelector selector, 
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign ?? throw new ArgumentNullException(nameof(currentCampaign));
    }

    public bool ShouldHandle(WorldChange change) => change is RulesetAction;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        if (change is not RulesetAction action)
        {
            return ChangeHandlerResult.Failure("Change is not a RulesetAction.");
        }
        
        var effectiveCampaign = _currentCampaign.CurrentCampaignName;
        var configId = _keys.Config(effectiveCampaign);
        var config = await context.Session.LoadAsync<CampaignConfig>(configId, ct)
                     ?? new CampaignConfig { Id = configId };

        var resolver = _selector.GetResolver(config.ActiveSystem);
        var output = await resolver.ResolveAsync(context, action, ct);

        foreach (var mutation in output.Mutations)
        {
            await context.Dispatcher.DispatchMutationAsync(context, mutation, ct);
        }

        return string.IsNullOrWhiteSpace(output.Result.Narrative)
            ? ChangeHandlerResult.Ok
            : new ChangeHandlerResult(true, output.Result.Narrative);
    }
}
