using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads class definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (dnd5e and pf2e both define "wizard" with different properties).
/// </summary>
public class ClassDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<ClassDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ClassDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    public ClassDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<ClassDefinition>(rulesetDataDirectory, embeddedAssembly, ["classes"], ClassDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, ClassDefinition> GetClassesForSystem(string system)
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

    /// <summary>
    /// Finds the best-matching class definition for a class name string using alias substring matching.
    /// Returns the definition whose longest alias is found in <paramref name="className"/>.
    /// </summary>
    public bool TryResolveClass(string system, string className, [NotNullWhen(true)] out ClassDefinition? classDef)
    {
        var classes = GetClassesForSystem(system);
        classDef = null;
        var bestMatchLen = 0;

        foreach (var def in classes.Values)
        {
            foreach (var alias in def.Aliases)
            {
                if (alias.Length > bestMatchLen &&
                    className.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    classDef = def;
                    bestMatchLen = alias.Length;
                }
            }
        }

        return classDef != null;
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
