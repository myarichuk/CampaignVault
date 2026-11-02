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
    private readonly Dictionary<(string, string), IReadOnlyDictionary<string, BackgroundDefinition>> _campaignCache = [];
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
            if (!_cache.TryGetValue(system, out var cached) || cached == null)
                _cache[system] = cached = _layers.Resolve(system);

            // The campaign's own backgrounds, when it has any (HomebrewScope), are layered on for that call only.
            var homebrew = HomebrewScope.Current;
            var entries = homebrew?.For(HomebrewKinds.Background, system) ?? [];
            if (homebrew is null || entries.Count == 0)
                return cached;

            if (_campaignCache.Count > 16)
                _campaignCache.Clear();
            var key = (homebrew.Stamp, system);
            if (!_campaignCache.TryGetValue(key, out var withHomebrew))
                _campaignCache[key] = withHomebrew = _layers.Resolve(system, [.. entries.Select(e => e.Yaml)]);
            return withHomebrew;
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
        {
            _cache.Clear();
            _campaignCache.Clear();
        }
    }
}