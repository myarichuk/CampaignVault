using System.Reflection;
using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>
/// Loads spell metadata from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class SpellDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, RulesetTemplateLoader<SpellDefinition>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, SpellDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SpellDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["spells"]);
        foreach (var (systemSlug, subfolder) in discovered)
        {
            Register(systemSlug, rulesetDataDirectory, systemSlug, subfolder, embeddedAssembly, logger);
        }
    }

    private void Register(
        string system,
        string rulesetDataDirectory,
        string systemSlug,
        string subfolder,
        Assembly embeddedAssembly,
        ILogger? logger)
    {
        _loaders[system] = new RulesetTemplateLoader<SpellDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.{subfolder}",
            logger);
    }

    public IReadOnlyDictionary<string, SpellDefinition> GetSpellsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, SpellDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<SpellDefinition>(
                name => raw.GetValueOrDefault(name),
                SpellDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string spellName, out SpellDefinition? spell)
    {
        var spells = GetSpellsForSystem(system);
        return spells.TryGetValue(spellName, out spell);
    }

    public IReadOnlyList<SpellDefinition> QuerySpells(
        string system,
        string? className = null,
        int? level = null,
        ClassDefinitionProvider? classProvider = null)
    {
        var spells = GetSpellsForSystem(system).Values;

        if (!string.IsNullOrWhiteSpace(className))
        {
            spells = spells.Where(s => SpellMatchesClass(s, className, system, classProvider));
        }

        if (level.HasValue)
        {
            spells = spells.Where(s => (s.Level ?? 0) == level.Value);
        }

        return spells
            .OrderBy(s => s.Level ?? 0)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool SpellMatchesClass(
        SpellDefinition spell,
        string className,
        string system,
        ClassDefinitionProvider? classProvider)
    {
        if (classProvider?.TryResolveClass(system, className, out var classDef) == true && classDef != null)
        {
            if (spell.Classes.Any(c => c.Equals(classDef.Name, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (classDef.Aliases.Any(alias =>
                    spell.Classes.Any(c => c.Equals(alias, StringComparison.OrdinalIgnoreCase))))
                return true;
        }

        var normalized = className.Trim();
        return spell.Classes.Any(c =>
            string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}