using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public interface IRulesetModuleSelector
{
    IRulesetModule GetModule(RulesetSystem system);
}

public class RulesetModuleSelector : IRulesetModuleSelector
{
    private readonly Dictionary<RulesetSystem, IRulesetModule> _modules;

    public RulesetModuleSelector(IEnumerable<IRulesetModule> modules)
    {
        _modules = modules?.ToDictionary(m => m.System) ?? new Dictionary<RulesetSystem, IRulesetModule>();

        foreach (var system in Enum.GetValues<RulesetSystem>())
        {
            if (!_modules.ContainsKey(system))
            {
                throw new InvalidOperationException($"Startup Validation Failed: No IRulesetModule registered for system: {system}");
            }
        }
    }

    public IRulesetModule GetModule(RulesetSystem system)
    {
        if (!_modules.TryGetValue(system, out var module))
        {
            throw new InvalidOperationException($"The requested ruleset system '{Enum.GetName(typeof(RulesetSystem), system)}' is not supported or not registered.");
        }

        return module;
    }
}