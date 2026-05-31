using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public interface IRulesetResolverSelector
{
    IRulesetResolver GetResolver(RulesetSystem system);
}

public class RulesetResolverSelector : IRulesetResolverSelector
{
    private readonly Dictionary<RulesetSystem, IRulesetResolver> _resolvers;

    public RulesetResolverSelector(IEnumerable<IRulesetResolver> resolvers)
    {
        _resolvers = resolvers?.ToDictionary(r => r.System) ?? new Dictionary<RulesetSystem, IRulesetResolver>();

        // Validate at startup that every expected RulesetSystem has exactly one resolver registered
        // Optional enum values could be skipped here if needed, but the current plan expects validation.
        foreach (var system in Enum.GetValues<RulesetSystem>())
        {
            if (!_resolvers.ContainsKey(system))
            {
                throw new InvalidOperationException($"Startup Validation Failed: No IRulesetResolver registered for system: {system}");
            }
        }
    }

    public IRulesetResolver GetResolver(RulesetSystem system)
    {
        if (!_resolvers.TryGetValue(system, out var resolver))
        {
            // Throw using a custom message rather than Enum.ToString() implicitly
            throw new InvalidOperationException($"The requested ruleset system '{Enum.GetName(typeof(RulesetSystem), system)}' is not supported or not registered.");
        }
        
        return resolver;
    }
}
