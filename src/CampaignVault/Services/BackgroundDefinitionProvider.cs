using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads background definitions from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class BackgroundDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, List<RulesetTemplateLoader<BackgroundDefinition>>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, BackgroundDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public BackgroundDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["backgrounds"], PluginDataRoots.Additional);
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

        list.Add(new RulesetTemplateLoader<BackgroundDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            embeddedPrefix,
            logger));
    }

    public IReadOnlyDictionary<string, BackgroundDefinition> GetBackgroundsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loaders) || loaders.Count == 0)
                return new Dictionary<string, BackgroundDefinition>();

            var raw = new Dictionary<string, BackgroundDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var loader in loaders)
            {
                foreach (var (name, def) in loader.Load())
                    raw[name] = def;
            }
            var resolver = new RulesetTemplateResolver<BackgroundDefinition>(
                name => raw.GetValueOrDefault(name),
                BackgroundDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

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