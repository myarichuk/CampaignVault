using Autofac;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Single Autofac module for CampaignVault. All service registration is convention-based
/// via <see cref="ConventionRegistration"/>.
/// </summary>
public class CampaignVaultModule : Autofac.Module
{
    private readonly string _rulesetDataDirectory;

    public CampaignVaultModule() : this(null) { }

    public CampaignVaultModule(string? rulesetDataDirectory)
    {
        _rulesetDataDirectory = rulesetDataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "RulesetData");
    }

    protected override void Load(ContainerBuilder builder)
    {
        ConventionRegistration.Register(builder, System.Reflection.Assembly.GetExecutingAssembly(), _rulesetDataDirectory);
    }
}