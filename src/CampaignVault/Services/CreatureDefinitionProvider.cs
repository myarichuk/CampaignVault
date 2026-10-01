using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads reference creature definitions from per-system YAML files, resolves inheritance, and
/// caches results. Creature data is available for dnd5e and pf2e rulesets.
/// </summary>
public class CreatureDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<CreatureDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, CreatureDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public CreatureDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<CreatureDefinition>(rulesetDataDirectory, embeddedAssembly, ["creatures"], CreatureDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, CreatureDefinition> GetCreaturesForSystem(string system)
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

    public bool TryGet(string system, string creatureName, [NotNullWhen(true)] out CreatureDefinition? creature)
    {
        var creatures = GetCreaturesForSystem(system);
        if (creatures.TryGetValue(creatureName, out creature))
        {
            return true;
        }

        // Handbook refs travel as file slugs (air_elemental) while templates are
        // keyed by display name (Air Elemental): compare separator-insensitively so
        // overlay refs, caller parameters, and catalog names all resolve.
        var wanted = NormalizeCreatureName(creatureName);
        foreach (var (name, candidate) in creatures)
        {
            if (NormalizeCreatureName(name) == wanted)
            {
                creature = candidate;
                return true;
            }
        }

        creature = null;
        return false;
    }

    internal static string NormalizeCreatureName(string name) =>
        name.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
