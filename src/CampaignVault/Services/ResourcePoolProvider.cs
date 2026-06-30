using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;

namespace CampaignVault.Services;

/// <summary>
/// Loads resource pool templates from per-system YAML files, resolves inheritance, and caches results.
/// Each system (dnd5e, pf2e, fallout2d20) has its own loader to prevent name collisions between systems
/// (e.g. dnd5e and pf2e both define "spell_slots_1" with different tables).
/// </summary>
public class ResourcePoolProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<ResourcePoolTemplate>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, ResourcePoolTemplate>?> _cache = new();
    private readonly object _lock = new();

    public ResourcePoolProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
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
        _loaders[system] = new RulesetTemplateLoader<ResourcePoolTemplate>(
            Path.Combine(rulesetDataDirectory, systemSlug, "pools"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.pools",
            logger);
    }

    public IReadOnlyDictionary<string, ResourcePoolTemplate> GetPoolsForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ResourcePoolTemplate>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ResourcePoolTemplate>(
                name => raw.TryGetValue(name, out var t) ? t : null,
                ResourcePoolTemplate.Merge);

            var resolved = raw.ToDictionary(
                kvp => kvp.Key,
                kvp => resolver.Resolve(kvp.Value),
                StringComparer.OrdinalIgnoreCase);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
