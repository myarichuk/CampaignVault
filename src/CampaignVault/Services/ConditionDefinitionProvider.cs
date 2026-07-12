using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads condition definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "frightened" with different properties).
/// </summary>
public class ConditionDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<ConditionDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, ConditionDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ConditionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        Register(RulesetSystem.Dnd5e, rulesetDataDirectory, "dnd5e", embeddedAssembly, logger);
        Register(RulesetSystem.Pathfinder2e, rulesetDataDirectory, "pf2e", embeddedAssembly, logger);
        Register(RulesetSystem.Fallout2d20, rulesetDataDirectory, "fallout2d20", embeddedAssembly, logger);
    }

    private void Register(
        RulesetSystem system,
        string rulesetDataDirectory,
        string systemSlug,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<ConditionDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "conditions"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.conditions",
            logger);
    }

    public IReadOnlyDictionary<string, ConditionDefinition> GetConditionsForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ConditionDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ConditionDefinition>(
                name => raw.GetValueOrDefault(name),
                ConditionDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(RulesetSystem system, string conditionName, out ConditionDefinition? condition)
    {
        var conditions = GetConditionsForSystem(system);
        return conditions.TryGetValue(conditionName, out condition);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
