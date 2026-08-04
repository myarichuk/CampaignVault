using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads resource pool templates from per-system YAML files, resolves inheritance, and caches results.
/// Each system (dnd5e, pf2e) has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "spell_slots_1" with different tables).
/// </summary>
public class ResourcePoolProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<ResourcePoolTemplate>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ResourcePoolTemplate>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ResourcePoolProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["pools"]);
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
        _loaders[system] = new RulesetTemplateLoader<ResourcePoolTemplate>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.{subfolder}",
            logger);
    }

    public IReadOnlyDictionary<string, ResourcePoolTemplate> GetPoolsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ResourcePoolTemplate>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ResourcePoolTemplate>(
                name => raw.GetValueOrDefault(name),
                ResourcePoolTemplate.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

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
