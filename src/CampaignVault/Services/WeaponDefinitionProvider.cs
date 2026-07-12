using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads reference weapon definitions from per-system YAML files, resolves inheritance, and caches
/// results. Only fallout2d20 currently ships weapons/ content — dnd5e/pf2e weapon stats live
/// entirely on Item documents (no weapons/ directory for those systems).
/// </summary>
public class WeaponDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<WeaponDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, WeaponDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public WeaponDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        Register(RulesetSystem.Fallout2d20, rulesetDataDirectory, "fallout2d20", embeddedAssembly, logger);
    }

    private void Register(
        RulesetSystem system,
        string rulesetDataDirectory,
        string systemSlug,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<WeaponDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "weapons"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.weapons",
            logger);
    }

    public IReadOnlyDictionary<string, WeaponDefinition> GetWeaponsForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, WeaponDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<WeaponDefinition>(
                name => raw.GetValueOrDefault(name),
                WeaponDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(RulesetSystem system, string weaponName, [NotNullWhen(true)] out WeaponDefinition? weapon)
    {
        var weapons = GetWeaponsForSystem(system);
        return weapons.TryGetValue(weaponName, out weapon);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
