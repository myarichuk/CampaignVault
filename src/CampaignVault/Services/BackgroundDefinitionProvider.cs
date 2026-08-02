using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads background definitions from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class BackgroundDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<BackgroundDefinition>> _loaders = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, BackgroundDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public BackgroundDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
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
        _loaders[system] = new RulesetTemplateLoader<BackgroundDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "backgrounds"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.backgrounds",
            logger);
    }

    public IReadOnlyDictionary<string, BackgroundDefinition> GetBackgroundsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, BackgroundDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<BackgroundDefinition>(
                name => raw.GetValueOrDefault(name),
                BackgroundDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string backgroundName, out BackgroundDefinition? background)
    {
        var backgrounds = GetBackgroundsForSystem(system);
        return backgrounds.TryGetValue(backgroundName, out background);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}