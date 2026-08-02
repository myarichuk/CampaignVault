using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads reference creature definitions from per-system YAML files, resolves inheritance, and
/// caches results. Creature data is available for dnd5e and pf2e rulesets.
/// </summary>
public class CreatureDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<CreatureDefinition>> _loaders = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, CreatureDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public CreatureDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        Register(RulesetSystem.Dnd5e, rulesetDataDirectory, "dnd5e", embeddedAssembly, logger);
        Register(RulesetSystem.Pathfinder2e, rulesetDataDirectory, "pf2e", embeddedAssembly, logger);
    }

    private void Register(
        string system,
        string rulesetDataDirectory,
        string systemSlug,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<CreatureDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "creatures"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.creatures",
            logger);
    }

    public IReadOnlyDictionary<string, CreatureDefinition> GetCreaturesForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, CreatureDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<CreatureDefinition>(
                name => raw.GetValueOrDefault(name),
                CreatureDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string creatureName, [NotNullWhen(true)] out CreatureDefinition? creature)
    {
        var creatures = GetCreaturesForSystem(system);
        return creatures.TryGetValue(creatureName, out creature);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
