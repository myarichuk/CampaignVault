using System.Reflection;
using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>
/// Loads race/ancestry definitions from per-system YAML files, resolves inheritance, and caches results.
/// D&amp;D 5e uses <c>races/</c>; PF2e uses <c>ancestries/</c>.
/// </summary>
public class RaceDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<RaceDefinition>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, RaceDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public RaceDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["races", "ancestries"]);
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
        _loaders[system] = new RulesetTemplateLoader<RaceDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.{subfolder}",
            logger);
    }

    public IReadOnlyDictionary<string, RaceDefinition> GetRacesForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, RaceDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<RaceDefinition>(
                name => raw.GetValueOrDefault(name),
                RaceDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

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