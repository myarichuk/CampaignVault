using CampaignVault.Rulesets.Creation;

namespace CampaignVault.Rulesets;

public interface IRulesetModuleSelector
{
    IRulesetModule GetModule(string system);
    bool IsRegistered(string system);
    IReadOnlyCollection<string> RegisteredSystems { get; }

    /// <summary>
    /// The system's own <see cref="ICharacterCreation"/>, if a plugin provides one (on its ruleset module or as its own
    /// class); null means the host's data-driven recipe applies.
    /// </summary>
    ICharacterCreation? GetCreation(string system) => null;
}

public class RulesetModuleSelector : IRulesetModuleSelector
{
    private readonly Dictionary<string, IRulesetModule> _modules;
    private readonly Dictionary<string, ICharacterCreation> _creations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public IReadOnlyCollection<string> RegisteredSystems => _modules.Keys;

    public RulesetModuleSelector(
        IEnumerable<IRulesetModule>? modules,
        ILogger? logger = null,
        IEnumerable<ICharacterCreation>? creations = null)
    {
        _logger = logger;
        foreach (var creation in creations ?? [])
            _creations[creation.System] = creation;

        if (modules == null)
        {
            _modules = new Dictionary<string, IRulesetModule>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _modules = new Dictionary<string, IRulesetModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in modules)
            {
                _modules[module.System] = module;
            }
        }
    }

    public bool IsRegistered(string system) => _modules.ContainsKey(system);

    public ICharacterCreation? GetCreation(string system) =>
        _creations.GetValueOrDefault(system)
        ?? (_modules.GetValueOrDefault(system) as ICharacterCreation);

    public IRulesetModule GetModule(string system)
    {
        if (!_modules.TryGetValue(system, out var module))
        {
            throw new InvalidOperationException(
                $"Ruleset system '{system}' has no registered IRulesetModule. " +
                $"Available systems: {string.Join(", ", _modules.Keys)}. " +
                $"Ensure the system's DLL is in the plugins directory (if it has custom code). " +
                $"Data-only plugins (YAML) should still load and degrade gracefully.");
        }

        return module;
    }
}