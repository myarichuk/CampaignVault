using CampaignVault.Data.Pressure;

namespace CampaignVault.Rulesets;

public interface IRulesetPressureContributor : IPressureContributor;

/// <summary>
/// Host-side ruleset extension: the SDK <see cref="IRulesetModule"/> contract plus
/// session-bound read-side pressures. First-party resolvers implement this;
/// out-of-tree plugin modules implement the SDK contract and contribute pressure
/// via IPluginGuidanceContributor / IPluginContextContributor instead.
/// </summary>
public interface IHostRulesetModule : IRulesetModule
{
    IEnumerable<IRulesetPressureContributor> PressureContributors { get; }
}
