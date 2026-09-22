using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads item definitions ("templates") from per-system YAML files, resolves inheritance, and
/// caches results. Not weapon/armor-specific — a "Kara-Tur weapons pack" or "mountaineering
/// equipment pack" is just more YAML files under <c>RulesetData/{system}/items/</c>.
/// </summary>
public class ItemDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<string, List<RulesetTemplateLoader<ItemDefinition>>> _loaders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, ItemDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ItemDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        var discovered = RulesetDataSystemDiscovery.Discover(rulesetDataDirectory, embeddedAssembly, ["items"], PluginDataRoots.Additional);
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

        // Only the first loader for a system pulls embedded host defaults; later plugin roots are disk-only.
        var embeddedPrefix = list.Count == 0
            ? $"CampaignVault.RulesetData.{systemSlug}.{subfolder}"
            : $"CampaignVault.RulesetData.__plugin__.{systemSlug}.{subfolder}";

        list.Add(new RulesetTemplateLoader<ItemDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, subfolder),
            embeddedAssembly,
            embeddedPrefix,
            logger));
    }

    public IReadOnlyDictionary<string, ItemDefinition> GetItemsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loaders) || loaders.Count == 0)
                return new Dictionary<string, ItemDefinition>();

            // Merge roots in registration order; later plugin templates last-wins on name.
            var raw = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var loader in loaders)
            {
                foreach (var (name, def) in loader.Load())
                    raw[name] = def;
            }

            var resolver = new RulesetTemplateResolver<ItemDefinition>(
                name => raw.GetValueOrDefault(name),
                ItemDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(string system, string itemName, out ItemDefinition? item)
    {
        var items = GetItemsForSystem(system);
        return items.TryGetValue(itemName, out item);
    }

    public IReadOnlyList<ItemDefinition> QueryItems(
        string system,
        string? nameQuery = null,
        Models.ItemCategory? category = null,
        string? tag = null)
    {
        var items = GetItemsForSystem(system).Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(nameQuery))
        {
            items = items.Where(i => i.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (category.HasValue)
        {
            items = items.Where(i => i.Category == category.Value);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            items = items.Where(i => i.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
        }

        return items
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
