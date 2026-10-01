using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads resource pool templates from per-system YAML files, resolves inheritance, and caches results.
/// Each system (dnd5e, pf2e) has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "spell_slots_1" with different tables).
/// </summary>
public class ResourcePoolProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<ResourcePoolTemplate> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ResourcePoolTemplate>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ResourcePoolProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<ResourcePoolTemplate>(rulesetDataDirectory, embeddedAssembly, ["pools"], ResourcePoolTemplate.Merge, logger);
    }

    public IReadOnlyDictionary<string, ResourcePoolTemplate> GetPoolsForSystem(string system)
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

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
