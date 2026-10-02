using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;

namespace CampaignVault.Services;

/// <summary>
/// Loads class progression definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (dnd5e and pf2e both define "fighter" with different properties).
/// </summary>
public class ProgressionDefinitionProvider : IRulesetYamlProvider
{
    private readonly RulesetContentLayers<ProgressionDefinition> _layers;
    private readonly RulesetContentLayers<ClassOptionDefinition> _optionLayers;
    private readonly Dictionary<string, IReadOnlyDictionary<string, ProgressionDefinition>?> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), IReadOnlyDictionary<string, ProgressionDefinition>> _campaignCache = [];
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    public ProgressionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null, IReadOnlyList<string>? pluginRoots = null)
    {
        _logger = logger;
        _layers = new RulesetContentLayers<ProgressionDefinition>(rulesetDataDirectory, embeddedAssembly, ["progressions"], ProgressionDefinition.Merge, logger, pluginRoots);
        _optionLayers = new RulesetContentLayers<ClassOptionDefinition>(rulesetDataDirectory, embeddedAssembly, ["classOptions"], ClassOptionDefinition.Merge, logger, pluginRoots);
    }

    public IReadOnlyDictionary<string, ProgressionDefinition> GetProgressionsForSystem(string system)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(system, out var cached) || cached == null)
            {
                cached = WithClassOptions(_layers.Resolve(system), system, []);
                _cache[system] = cached;
            }

            // The campaign's own class options (its subclasses), when it has any (HomebrewScope), join for that call only.
            var homebrew = HomebrewScope.Current;
            var entries = homebrew?.For(HomebrewKinds.ClassOption, system) ?? [];
            if (homebrew is null || entries.Count == 0)
                return cached;

            if (_campaignCache.Count > 16)
                _campaignCache.Clear();
            var key = (homebrew.Stamp, system);
            if (!_campaignCache.TryGetValue(key, out var withHomebrew))
                _campaignCache[key] = withHomebrew = WithClassOptions(_layers.Resolve(system), system, [.. entries.Select(e => e.Yaml)]);
            return withHomebrew;
        }
    }

    /// <summary>
    /// Adds the class option files (plugin subclasses, patrons) to the progressions they name, under the choice they name. An
    /// option whose class has no progression is skipped with a warning; one with the id of a shipped option replaces nothing
    /// (the shipped one stays: patch it, or hide it with <c>hideOptions+:</c>).
    /// </summary>
    private IReadOnlyDictionary<string, ProgressionDefinition> WithClassOptions(
        IReadOnlyDictionary<string, ProgressionDefinition> progressions, string system, IReadOnlyList<string> campaignYaml)
    {
        var options = _optionLayers.Resolve(system, campaignYaml).Values.Where(o => !o.Hidden).ToList();
        if (options.Count == 0)
            return progressions;

        var result = new Dictionary<string, ProgressionDefinition>(progressions, StringComparer.OrdinalIgnoreCase);
        foreach (var option in options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = result.Keys.FirstOrDefault(k => string.Equals(k, option.Class, StringComparison.OrdinalIgnoreCase))
                      ?? result.Where(kv => kv.Value.Aliases.Contains(option.Class, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).FirstOrDefault();
            if (key is null)
            {
                _logger?.LogWarning("Class option '{Name}' ({System}) names class '{Class}', which has no progression; skipped.",
                    option.Name, system, option.Class);
                continue;
            }

            var progression = result[key];
            var extra = progression.ExtraOptions.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (!extra.TryGetValue(option.Choice, out var list))
                extra[option.Choice] = list = [];
            list.Add(option.ToOption());
            result[key] = progression with { ExtraOptions = extra };
        }

        return result;
    }

    /// <summary>
    /// Gets the progression definition for a specific class in a system.
    /// </summary>
    public bool TryGetProgression(string system, string className, [NotNullWhen(true)] out ProgressionDefinition? progression)
    {
        var progressions = GetProgressionsForSystem(system);
        progression = null;

        // First try exact match (case-insensitive)
        foreach (var kvp in progressions)
        {
            if (string.Equals(kvp.Key, className, StringComparison.OrdinalIgnoreCase))
            {
                progression = kvp.Value;
                return true;
            }
        }

        // Then try alias match via ClassDefinitionProvider logic
        foreach (var kvp in progressions)
        {
            foreach (var alias in kvp.Value.Aliases)
            {
                if (className.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    progression = kvp.Value;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the level definition for a specific class at a specific level.
    /// </summary>
    public LevelDefinition? GetLevelDefinition(string system, string className, int level)
    {
        if (TryGetProgression(system, className, out var progression))
        {
            progression.Levels.TryGetValue(level, out var levelDef);
            return levelDef;
        }
        return null;
    }

    /// <summary>
    /// Gets all pending choices for a character leveling up to the specified level.
    /// </summary>
    public List<LevelUpChoiceDefinition> GetPendingChoices(string system, string className, int newLevel)
    {
        var choices = new List<LevelUpChoiceDefinition>();
        
        if (TryGetProgression(system, className, out var progression))
        {
            if (progression.Levels.TryGetValue(newLevel, out var levelDef))
            {
                choices.AddRange(levelDef.Choices);
            }
        }
        return choices;
    }

    public void Reload()
    {
        lock (_lock)
        {
            _cache.Clear();
            _campaignCache.Clear();
        }
    }
}