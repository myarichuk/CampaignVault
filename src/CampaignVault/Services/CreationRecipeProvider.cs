using System.Reflection;
using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>
/// Loads character builder recipes (<c>creation/</c>) and stat block schemas (<c>statblocks/</c>) per system, layered
/// like every other template kind, so plugin roots and <c>patches:</c> apply to them too.
/// </summary>
public class CreationRecipeProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<CreationRecipe> _recipes;
    private readonly RulesetContentLayers<StatBlockSchema> _statBlocks;
    private readonly Dictionary<string, IReadOnlyDictionary<string, CreationRecipe>> _recipeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, StatBlockSchema>> _statBlockCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public CreationRecipeProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
        : this(rulesetDataDirectory, embeddedAssembly, logger, null)
    {
    }

    /// <summary>For tests: explicit plugin roots instead of <c>PluginDataRoots.Additional</c>.</summary>
    internal CreationRecipeProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger, IReadOnlyList<string>? pluginRoots)
    {
        _recipes = new RulesetContentLayers<CreationRecipe>(rulesetDataDirectory, embeddedAssembly, ["creation"], CreationRecipe.Merge, logger, pluginRoots);
        _statBlocks = new RulesetContentLayers<StatBlockSchema>(rulesetDataDirectory, embeddedAssembly, ["statblocks"], StatBlockSchema.Merge, logger, pluginRoots);
    }

    /// <summary>Recipes for a system by kind (<c>pc</c>, <c>companion</c>); empty when the system ships none.</summary>
    public IReadOnlyDictionary<string, CreationRecipe> GetRecipesForSystem(string system)
    {
        lock (_lock)
        {
            if (!_recipeCache.TryGetValue(system, out var recipes))
            {
                recipes = _recipes.Resolve(system);
                _recipeCache[system] = recipes;
            }

            return recipes;
        }
    }

    public bool TryGetRecipe(string system, string kind, out CreationRecipe? recipe) =>
        GetRecipesForSystem(system).TryGetValue(kind, out recipe);

    public IReadOnlyDictionary<string, StatBlockSchema> GetStatBlocksForSystem(string system)
    {
        lock (_lock)
        {
            if (!_statBlockCache.TryGetValue(system, out var schemas))
            {
                schemas = _statBlocks.Resolve(system);
                _statBlockCache[system] = schemas;
            }

            return schemas;
        }
    }

    /// <summary>Every system with at least one recipe on disk, embedded or in a plugin root.</summary>
    public IReadOnlyCollection<string> Systems => _recipes.Systems;

    public void Reload()
    {
        lock (_lock)
        {
            _recipeCache.Clear();
            _statBlockCache.Clear();
        }
    }
}
