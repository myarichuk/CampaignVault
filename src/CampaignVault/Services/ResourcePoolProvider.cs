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
    private readonly Dictionary<string, List<RulesetTemplateLoader<ResourcePoolTemplate>>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ResourcePoolTemplate>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ResourcePoolProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["pools"], PluginDataRoots.Additional);
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

        list.Add(new RulesetTemplateLoader<ResourcePoolTemplate>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            embeddedPrefix,
            logger));
    }

    public IReadOnlyDictionary<string, ResourcePoolTemplate> GetPoolsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loaders) || loaders.Count == 0)
                return new Dictionary<string, ResourcePoolTemplate>();

            var raw = new Dictionary<string, ResourcePoolTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var loader in loaders)
            {
                foreach (var (name, def) in loader.Load())
                    raw[name] = def;
            }
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
