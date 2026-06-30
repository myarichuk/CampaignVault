namespace CampaignVault.Data.Templates;

/// <summary>
/// Marker for YAML-backed ruleset data providers under RulesetData/.
/// Autofac scans the assembly and registers all concrete implementors as singletons.
/// </summary>
public interface IRulesetYamlProvider;