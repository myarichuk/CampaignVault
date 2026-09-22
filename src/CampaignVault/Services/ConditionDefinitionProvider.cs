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
    private readonly Dictionary<string, List<RulesetTemplateLoader<ConditionDefinition>>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ConditionDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ConditionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["conditions"], PluginDataRoots.Additional);
        foreach (var (systemSlug, subfolder, diskRoot) in discovered)
        {
            Register(systemSlug, diskRoot, systemSlug, subfolder, embeddedAssembly, logger);
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
        if (!_loaders.TryGetValue(system, out var list))
        {
            list = [];
            _loaders[system] = list;
        }

        // First loader pulls embedded host defaults; later plugin roots are disk-only overlays.
        var embeddedPrefix = list.Count == 0
            ? $"CampaignVault.RulesetData.{systemSlug}.{subfolder}"
            : $"CampaignVault.RulesetData.__plugin__.{systemSlug}.{subfolder}";

        list.Add(new RulesetTemplateLoader<ConditionDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            embeddedPrefix,
            logger));
    }

    public IReadOnlyDictionary<string, ConditionDefinition> GetConditionsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loaders) || loaders.Count == 0)
                return new Dictionary<string, ConditionDefinition>();

            var raw = new Dictionary<string, ConditionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var loader in loaders)
            {
                foreach (var (name, def) in loader.Load())
                    raw[name] = def;
            }
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
