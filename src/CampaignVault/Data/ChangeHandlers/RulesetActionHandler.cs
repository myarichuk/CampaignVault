using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RulesetActionHandler : IWorldChangeHandler
{
    private readonly IRulesetResolverSelector _selector;

    public RulesetActionHandler(IRulesetResolverSelector selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }

    public bool ShouldHandle(WorldChange change) => change is RulesetAction;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        if (change is not RulesetAction action)
        {
            return ChangeHandlerResult.Failure("Change is not a RulesetAction.");
        }
        
        var config = await context.Session.LoadAsync<CampaignConfig>("campaign/config", ct)
                     ?? new CampaignConfig();

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
