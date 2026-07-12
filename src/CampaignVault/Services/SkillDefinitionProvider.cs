using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Loads reference skill definitions from per-system YAML files, resolves inheritance, and
/// caches results. Only fallout2d20 currently ships skills/ content.
/// </summary>
public class SkillDefinitionProvider : IRulesetYamlProvider
{
    private readonly Dictionary<RulesetSystem, RulesetTemplateLoader<SkillDefinition>> _loaders = new();
    private readonly Dictionary<RulesetSystem, IReadOnlyDictionary<string, SkillDefinition>?> _cache = new();
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    public SkillDefinitionProvider(string rulesetDataDirectory, Assembly embeddedAssembly, ILogger? logger = null)
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
        _loaders[system] = new RulesetTemplateLoader<SkillDefinition>(
            Path.Combine(rulesetDataDirectory, systemSlug, "skills"),
            embeddedAssembly,
            $"CampaignVault.RulesetData.{systemSlug}.skills",
            logger);
    }

    public IReadOnlyDictionary<string, SkillDefinition> GetSkillsForSystem(RulesetSystem system)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(system, out var cached) && cached != null)
                return cached;

            if (!_loaders.TryGetValue(system, out var loader))
                return new Dictionary<string, SkillDefinition>();

            var raw = loader.Load();
            var resolver = new RulesetTemplateResolver<SkillDefinition>(
                name => raw.GetValueOrDefault(name),
                SkillDefinition.Merge);

            var resolved = resolver.ResolveAll(raw, _logger);

            _cache[system] = resolved;
            return resolved;
        }
    }

    public bool TryGet(RulesetSystem system, string skillName, [NotNullWhen(true)] out SkillDefinition? skill)
    {
        var skills = GetSkillsForSystem(system);
        return skills.TryGetValue(skillName, out skill);
    }

    public void Reload()
    {
        lock (_lock)
            _cache.Clear();
    }
}
