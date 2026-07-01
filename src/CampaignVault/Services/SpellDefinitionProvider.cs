using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads spell metadata from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class SpellDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<SpellDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, SpellDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SpellDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
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
        _loaders[system] = new RulesetTemplateLoader<SpellDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "spells"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.spells",
            logger);
    }

    public IReadOnlyDictionary<string, SpellDefinition> GetSpellsForSystem(RulesetSystem system)
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

    public bool TryGet(RulesetSystem system, string spellName, out SpellDefinition? spell)
    {
        var spells = GetSpellsForSystem(system);
        return spells.TryGetValue(spellName, out spell);
    }

    public IReadOnlyList<SpellDefinition> QuerySpells(
        RulesetSystem system,
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
        RulesetSystem system,
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