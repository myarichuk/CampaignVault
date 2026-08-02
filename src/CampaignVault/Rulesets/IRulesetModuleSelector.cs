using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public interface IRulesetModuleSelector
{
    IRulesetModule GetModule(string system);
}

public class RulesetModuleSelector : IRulesetModuleSelector
{
    private readonly Dictionary<string, IRulesetModule> _modules;

    public RulesetModuleSelector(IEnumerable<IRulesetModule>? modules)
    {
        if (modules == null)
        {
            _modules = new Dictionary<string, IRulesetModule>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _modules = new Dictionary<string, IRulesetModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in modules)
            {
                _modules[module.System] = module;
            }
        }
    }

    public IRulesetModule GetModule(string system)
    {
        if (!_modules.TryGetValue(system, out var module))
        {
            throw new InvalidOperationException(
                $"Ruleset system '{system}' is not supported or not registered. " +
                $"Available systems: {string.Join(", ", _modules.Keys)}");
        }

        return module;
    }
}