using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads race/ancestry definitions from per-system YAML files, resolves inheritance, and caches results.
/// D&amp;D 5e uses <c>races/</c>; PF2e uses <c>ancestries/</c>.
/// </summary>
public class RaceDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<RaceDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, RaceDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public RaceDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<RaceDefinition>(rulesetDataDirectory, embeddedAssembly, ["races", "ancestries"], RaceDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, RaceDefinition> GetRacesForSystem(string system)
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

    public bool TryGet(string system, string raceName, out RaceDefinition? race)
    {
        var races = GetRacesForSystem(system);
        return races.TryGetValue(raceName, out race);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}