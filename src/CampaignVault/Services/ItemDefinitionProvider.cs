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
    private readonly RulesetContentLayers<ItemDefinition> _layers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ItemDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NameSearchIndex<ItemDefinition>> _nameIndexes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public ItemDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<ItemDefinition>(rulesetDataDirectory, embeddedAssembly, ["items"], ItemDefinition.Merge, logger);
    }

    public IReadOnlyDictionary<string, ItemDefinition> GetItemsForSystem(string system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            var resolved = _layers.Resolve(system);

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
        string? category = null,
        string? tag = null)
    {
        var hasNameQuery = !string.IsNullOrWhiteSpace(nameQuery);
        var scores = new Dictionary<ItemDefinition, int>();
        IEnumerable<ItemDefinition> items;
        if (hasNameQuery)
        {
            var hits = GetNameIndex(system).Search(nameQuery);
            scores = hits.ToDictionary(h => h.Item, h => h.Score);
            items = hits.Select(h => h.Item);
        }
        else
        {
            items = GetItemsForSystem(system).Values;
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            items = items.Where(i => string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            items = items.Where(i => i.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
        }

        // Name search: best match first. Otherwise alphabetical.
        return
        [
            .. (hasNameQuery
                ? items.OrderByDescending(i => scores[i]).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        ];
    }

    private NameSearchIndex<ItemDefinition> GetNameIndex(string system)
    {
        var items = GetItemsForSystem(system);
        lock (_lock)
        {
            if (!_nameIndexes.TryGetValue(system, out var index))
            {
                index = NameSearchIndex<ItemDefinition>.Build(items.Values, i => i.Name);
                _nameIndexes[system] = index;
            }

            return index;
        }
    }

    /// <summary>
    /// Distinct tags across every item template (built-in + plugin packs) for a system, sorted.
    /// Lets a caller check the existing tag vocabulary before inventing a new one — avoids
    /// near-duplicate tags (e.g. "exotic" vs "rare") that silently break <see cref="QueryItems"/>'s
    /// exact-match tag filter.
    /// </summary>
    public IReadOnlyList<string> GetDistinctTags(string system) =>
    [
        .. GetItemsForSystem(system).Values
            .SelectMany(i => i.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
    ];

    public void Reload()
    {
        lock (_lock)
        {
            _cache.Clear();
            _nameIndexes.Clear();
        }
    }
}
