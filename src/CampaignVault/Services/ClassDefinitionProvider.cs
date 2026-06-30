using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;

namespace CampaignVault.Services;

/// <summary>
/// Loads class definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "wizard" with different properties).
/// </summary>
public class ClassDefinitionProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<ClassDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, ClassDefinition>?> _cache = new();
    private readonly object _lock = new();

    public ClassDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        Register(RulesetSystem.Dnd5e, rulesetDataDirectory, "dnd5e", embeddedAssembly, logger);
        Register(RulesetSystem.Pathfinder2e, rulesetDataDirectory, "pf2e", embeddedAssembly, logger);
    }

    private void Register(
        RulesetSystem system,
        string rulesetDataDirectory,
        string systemSlug,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<ClassDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "classes"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.classes",
            logger);
    }

    public IReadOnlyDictionary<string, ClassDefinition> GetClassesForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ClassDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ClassDefinition>(
                name => raw.TryGetValue(name, out var t) ? t : null,
                ClassDefinition.Merge);

            var resolved = raw.ToDictionary(
                kvp => kvp.Key,
                kvp => resolver.Resolve(kvp.Value),
                StringComparer.OrdinalIgnoreCase);

            _cache[system] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// Finds the best-matching class definition for a class name string using alias substring matching.
    /// Returns the definition whose longest alias is found in <paramref name="className"/>.
    /// </summary>
    public bool TryResolveClass(RulesetSystem system, string className, out ClassDefinition? classDef)
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
