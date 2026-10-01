using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads feat definitions from per-system YAML files, resolves inheritance, and caches results.
/// D&amp;D 5e and PF2e use <c>feats/</c>.
/// </summary>
public class FeatDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<FeatDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, FeatDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public FeatDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<FeatDefinition>(rulesetDataDirectory, embeddedAssembly, ["feats"], FeatDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, FeatDefinition> GetFeatsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            var resolved = _layers.Resolve(system);

            // A typo'd effect kind in shipped or plugin YAML would silently do nothing; say so once, at load.
            foreach (var (name, def) in resolved)
            {
                var problems = CampaignVault.Rulesets.FeatEffectRules.Validate(def.Effects);
                if (problems.Count > 0)
                    _logger?.LogWarning("Feat '{Feat}' ({System}) has invalid effects: {Problems}", name, system, string.Join(" ", problems));
            }

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string featName, [NotNullWhen(true)] out FeatDefinition? feat)
    {
        var feats = GetFeatsForSystem(system);
        return feats.TryGetValue(featName, out feat);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}