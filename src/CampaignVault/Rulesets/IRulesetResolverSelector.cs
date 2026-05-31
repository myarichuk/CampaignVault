using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public interface IRulesetResolverSelector
{
    IRulesetResolver GetResolver(RulesetSystem system);
}

public class RulesetResolverSelector : IRulesetResolverSelector
{
    private readonly IEnumerable<IRulesetResolver> _resolvers;

    public RulesetResolverSelector(IEnumerable<IRulesetResolver> resolvers)
    {
        _resolvers = resolvers ?? Enumerable.Empty<IRulesetResolver>();
    }

    public IRulesetResolver GetResolver(RulesetSystem system)
    {
        var resolver = _resolvers.FirstOrDefault(r => r.System == system);
        if (resolver == null)
            throw new InvalidOperationException($"No IRulesetResolver registered for system: {system}");
        
        return resolver;
    }
}
