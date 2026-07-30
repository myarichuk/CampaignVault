using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads class progression definitions from per-system YAML files, resolves inheritance, and caches results.
/// Each system has its own loader to prevent name collisions between systems
/// (dnd5e and pf2e both define "fighter" with different properties).
/// </summary>
public class ProgressionDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<ProgressionDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, ProgressionDefinition>?> _cache = new();
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    public ProgressionDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
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
        _loaders[system] = new RulesetTemplateLoader<ProgressionDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "progressions"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.progressions",
            logger);
    }

    public IReadOnlyDictionary<string, ProgressionDefinition> GetProgressionsForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, ProgressionDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<ProgressionDefinition>(
                raw.GetValueOrDefault,
                ProgressionDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// Gets the progression definition for a specific class in a system.
    /// </summary>
    public bool TryGetProgression(RulesetSystem system, string className, [NotNullWhen(true)] out ProgressionDefinition? progression)
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
    public LevelDefinition? GetLevelDefinition(RulesetSystem system, string className, int level)
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
    public List<LevelUpChoiceDefinition> GetPendingChoices(RulesetSystem system, string className, int newLevel)
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
            _cache.Clear();
    }
}