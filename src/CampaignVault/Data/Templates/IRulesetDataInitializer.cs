namespace CampaignVault.Data.Templates;

/// <summary>
/// Marker for services that compose YAML-backed ruleset data providers at runtime.
/// Autofac scans and registers all concrete implementors as self + per-lifetime-scope.
/// </summary>
public interface IRulesetDataInitializer;