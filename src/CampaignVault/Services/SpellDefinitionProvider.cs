using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads spell metadata from per-system YAML files, resolves inheritance, and caches results.
/// </summary>
public class SpellDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, List<RulesetTemplateLoader<SpellDefinition>>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, SpellDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NameSearchIndex<SpellDefinition>> _nameIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SpellDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["spells"], PluginDataRoots.Additional);
        foreach (var (systemSlug, subfolder, diskRoot) in discovered)
        {
            Register(systemSlug, diskRoot, systemSlug, subfolder, embeddedAssembly, logger);
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
        if (!_loaders.TryGetValue(system, out var list))
        {
            list = [];
            _loaders[system] = list;
        }

        // First loader pulls embedded host defaults; later plugin roots are disk-only overlays.
        var embeddedPrefix = list.Count == 0
            ? $"CampaignVault.RulesetData.{systemSlug}.{subfolder}"
            : $"CampaignVault.RulesetData.__plugin__.{systemSlug}.{subfolder}";

        list.Add(new RulesetTemplateLoader<SpellDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            embeddedPrefix,
            logger));
    }

    public IReadOnlyDictionary<string, SpellDefinition> GetSpellsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loaders) || loaders.Count == 0)
                return new Dictionary<string, SpellDefinition>();

            var raw = new Dictionary<string, SpellDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var loader in loaders)
            {
                foreach (var (name, def) in loader.Load())
                    raw[name] = def;
            }
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
        ClassDefinitionProvider? classProvider = null,
        string? nameQuery = null) =>
        [.. QuerySpellsRanked(system, className, level, classProvider, nameQuery).Select(h => h.Item)];

    /// <summary>
    /// Filters by class and level. Without <paramref name="nameQuery"/> the order is level then name (score 0);
    /// with it, only name matches are returned, best match first.
    /// </summary>
    public IReadOnlyList<NameSearchHit<SpellDefinition>> QuerySpellsRanked(
        string system,
        string? className = null,
        int? level = null,
        ClassDefinitionProvider? classProvider = null,
        string? nameQuery = null)
    {
        IEnumerable<NameSearchHit<SpellDefinition>> hits;
        var hasNameQuery = !string.IsNullOrWhiteSpace(nameQuery);
        if (hasNameQuery)
        {
            hits = GetNameIndex(system).Search(nameQuery);
        }
        else
        {
            hits = GetSpellsForSystem(system).Values.Select(s => new NameSearchHit<SpellDefinition>(s, 0));
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            hits = hits.Where(h => SpellMatchesClass(h.Item, className, system, classProvider));
        }

        if (level.HasValue)
        {
            hits = hits.Where(h => (h.Item.Level ?? 0) == level.Value);
        }

        var ordered = hasNameQuery
            ? hits.OrderByDescending(h => h.Score)
                .ThenBy(h => h.Item.Level ?? 0)
                .ThenBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase)
            : hits.OrderBy(h => h.Item.Level ?? 0)
                .ThenBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase);
        return [.. ordered];
    }

    private NameSearchIndex<SpellDefinition> GetNameIndex(string system)
    {
        var spells = GetSpellsForSystem(system);
        lock (_lock)
        {
            if (!_nameIndexes.TryGetValue(system, out var index))
            {
                index = NameSearchIndex<SpellDefinition>.Build(spells.Values, s => s.Name);
                _nameIndexes[system] = index;
            }

            return index;
        }
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
        {
            _cache.Clear();
            _nameIndexes.Clear();
        }
    }
}