using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads condition definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "frightened" with different properties).
/// </summary>
public class ConditionDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<ConditionDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ConditionDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ConditionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<ConditionDefinition>(rulesetDataDirectory, embeddedAssembly, ["conditions"], ConditionDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, ConditionDefinition> GetConditionsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            var resolved = _layers.Resolve(system);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string conditionName, out ConditionDefinition? condition)
    {
        var conditions = GetConditionsForSystem(system);
        return conditions.TryGetValue(conditionName, out condition);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
