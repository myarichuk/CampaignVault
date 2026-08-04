using System.Reflection;
using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>
/// Loads condition definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "frightened" with different properties).
/// </summary>
public class ConditionDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<ConditionDefinition>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ConditionDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ConditionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["conditions"]);
        foreach (var (systemSlug, subfolder) in discovered)
        {
            Register(systemSlug, rulesetDataDirectory, systemSlug, subfolder, embeddedAssembly, logger);
        }
    }

    private void Register(
        string system,
        string rulesetDataDirectory,
        string systemSlug,
        string subfolder,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<ConditionDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.{subfolder}",
            logger);
    }

    public IReadOnlyDictionary<string, ConditionDefinition> GetConditionsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ConditionDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ConditionDefinition>(
                name => raw.GetValueOrDefault(name),
                ConditionDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

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
