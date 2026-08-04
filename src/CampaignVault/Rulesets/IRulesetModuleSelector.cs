namespace CampaignVault.Rulesets;

public interface IRulesetModuleSelector
{
    IRulesetModule GetModule(string system);
    bool IsRegistered(string system);
    IReadOnlyCollection<string> RegisteredSystems { get; }
}

public class RulesetModuleSelector : IRulesetModuleSelector
{
    private readonly Dictionary<string, IRulesetModule> _modules;
    private readonly ILogger? _logger;

    public IReadOnlyCollection<string> RegisteredSystems => _modules.Keys;

    public RulesetModuleSelector(IEnumerable<IRulesetModule>? modules, ILogger? logger = null)
    {
        _logger = logger;
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

    public bool IsRegistered(string system) => _modules.ContainsKey(system);

    public IRulesetModule GetModule(string system)
    {
        if (!_modules.TryGetValue(system, out var module))
        {
            throw new InvalidOperationException(
                $"Ruleset system '{system}' has no registered IRulesetModule. " +
                $"Available systems: {string.Join(", ", _modules.Keys)}. " +
                $"Ensure the system's DLL is in the plugins directory (if it has custom code). " +
                $"Data-only plugins (YAML) should still load and degrade gracefully.");
        }

        return module;
    }
}