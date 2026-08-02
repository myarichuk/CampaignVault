using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads feat definitions from per-system YAML files, resolves inheritance, and caches results.
/// D&amp;D 5e and PF2e use <c>feats/</c>.
/// </summary>
public class FeatDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<FeatDefinition>> _loaders = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, FeatDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public FeatDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        Register(RulesetSystem.Dnd5e, rulesetDataDirectory, "dnd5e", "feats", embeddedAssembly, logger);
        Register(RulesetSystem.Pathfinder2e, rulesetDataDirectory, "pf2e", "feats", embeddedAssembly, logger);
    }

    private void Register(
        string system,
        string rulesetDataDirectory,
        string systemSlug,
        string subfolder,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<FeatDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.{subfolder}",
            logger);
    }

    public IReadOnlyDictionary<string, FeatDefinition> GetFeatsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, FeatDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<FeatDefinition>(
                name => raw.GetValueOrDefault(name),
                FeatDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

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