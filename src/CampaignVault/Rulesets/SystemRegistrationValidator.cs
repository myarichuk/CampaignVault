using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// Validates that a system has appropriate support (YAML data and/or IRulesetModule).
/// Used at campaign load time to provide clear error messages for misconfigured plugins.
/// </summary>
public interface ISystemRegistrationValidator
{
    /// <summary>
    /// Validate that a system can be used for campaigns.
    /// </summary>
    ValidationResult Validate(string systemId);
}

public record ValidationResult(
    bool IsValid,
    string SystemId,
    bool HasModule,
    bool HasYamlData,
    string? ErrorMessage = null);

internal class SystemRegistrationValidator : ISystemRegistrationValidator
{
    private readonly IRulesetModuleSelector _rulesets;
    private readonly SpellDefinitionProvider? _spells;
    private readonly ClassDefinitionProvider? _classes;
    private readonly RaceDefinitionProvider? _races;
    private readonly ILogger? _logger;

    public SystemRegistrationValidator(
        IRulesetModuleSelector rulesets,
        SpellDefinitionProvider? spells = null,
        ClassDefinitionProvider? classes = null,
        RaceDefinitionProvider? races = null,
        ILogger? logger = null)
    {
        _rulesets = rulesets;
        _spells = spells;
        _classes = classes;
        _races = races;
        _logger = logger;
    }

    public ValidationResult Validate(string systemId)
    {
        var hasModule = _rulesets.IsRegistered(systemId);
        var hasYamlData = HasAnyYamlData(systemId);

        if (!hasModule && !hasYamlData)
        {
            var msg = $"System '{systemId}' is neither registered (no IRulesetModule) " +
                      $"nor has YAML data. Available systems: {string.Join(", ", _rulesets.RegisteredSystems)}";
            _logger?.LogError(msg);
            return new ValidationResult(false, systemId, false, false, msg);
        }

        if (!hasModule)
        {
            var msg = $"System '{systemId}' has YAML data but no IRulesetModule. " +
                      $"Using as data-only plugin with base calculation rules.";
            _logger?.LogWarning(msg);
        }

        if (!hasYamlData)
        {
            var msg = $"System '{systemId}' is registered but has no YAML data. " +
                      $"Campaigns can use it, but no predefined spells/races/classes/etc will be available.";
            _logger?.LogInformation(msg);
        }

        return new ValidationResult(true, systemId, hasModule, hasYamlData);
    }

    private bool HasAnyYamlData(string systemId)
    {
        try
        {
            return (_spells?.GetSpellsForSystem(systemId).Any() ?? false)
                || (_classes?.GetClassesForSystem(systemId).Any() ?? false)
                || (_races?.GetRacesForSystem(systemId).Any() ?? false);
        }
        catch
        {
            return false;
        }
    }
}
