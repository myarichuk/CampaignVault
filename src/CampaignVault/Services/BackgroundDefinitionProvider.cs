using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads background definitions from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class BackgroundDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<BackgroundDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, BackgroundDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public BackgroundDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<BackgroundDefinition>(rulesetDataDirectory, embeddedAssembly, ["backgrounds"], BackgroundDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, BackgroundDefinition> GetBackgroundsForSystem(string system)
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

    public bool TryGet(string system, string backgroundName, out BackgroundDefinition? background)
    {
        var backgrounds = GetBackgroundsForSystem(system);
        return backgrounds.TryGetValue(backgroundName, out background);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}