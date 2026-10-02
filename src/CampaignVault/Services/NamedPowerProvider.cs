using System.Reflection;
using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>Loads named powers (gods, patrons, bloodlines) from per-system <c>powers/</c> folders. None ship with the host.</summary>
public class NamedPowerProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<NamedPowerDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, NamedPowerDefinition>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), IReadOnlyDictionary<string, NamedPowerDefinition>> _campaignCache = [];
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    public NamedPowerProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null, IReadOnlyList<string>? pluginRoots = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<NamedPowerDefinition>(rulesetDataDirectory, embeddedAssembly, ["powers"], NamedPowerDefinition.Merge, logger, pluginRoots);
    }

    public IReadOnlyDictionary<string, NamedPowerDefinition> GetPowersForSystem(string system)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(system, out var cached))
                _cache[system] = cached = Resolve(system, []);

            // The campaign's own powers, when it has any (HomebrewScope), are layered on for that call only.
            var homebrew = HomebrewScope.Current;
            var entries = homebrew?.For(HomebrewKinds.Power, system) ?? [];
            if (homebrew is null || entries.Count == 0)
                return cached;

            if (_campaignCache.Count > 16)
                _campaignCache.Clear();
            var key = (homebrew.Stamp, system);
            if (!_campaignCache.TryGetValue(key, out var withHomebrew))
                _campaignCache[key] = withHomebrew = Resolve(system, [.. entries.Select(e => e.Yaml)]);
            return withHomebrew;
        }
    }

    private IReadOnlyDictionary<string, NamedPowerDefinition> Resolve(string system, IReadOnlyList<string> campaignYaml)
    {
        var resolved = _layers.Resolve(system, campaignYaml);
        foreach (var power in resolved.Values.Where(p => !NamedPowerDefinition.Types.Contains(p.Type)))
            _logger?.LogWarning("Named power '{Name}' has type '{Type}'; use deity, patron or lineage.", power.Name, power.Type);
        return resolved;
    }

    public bool TryGet(string system, string name, out NamedPowerDefinition? power) =>
        GetPowersForSystem(system).TryGetValue(name, out power);

    public void Reload()
    {
        lock (_lock)
        {
            _cache.Clear();
            _campaignCache.Clear();
        }
    }
}
