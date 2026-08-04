using Autofac;
using System.Reflection;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Single Autofac module for CampaignVault. All service registration is convention-based
/// via <see cref="ConventionRegistration"/>.
///
/// Task 4.7: Loads plugin assemblies from the Plugins directory and registers their
/// IRulesetModule and other convention-matched implementations.
/// </summary>
public class CampaignVaultModule : Autofac.Module
{
    private readonly string _rulesetDataDirectory;
    private readonly string? _pluginDirectory;

    public CampaignVaultModule() : this(null, null) { }

    public CampaignVaultModule(string? rulesetDataDirectory, string? pluginDirectory = null)
    {
        _rulesetDataDirectory = rulesetDataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "RulesetData");
        _pluginDirectory = pluginDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
    }

    protected override void Load(ContainerBuilder builder)
    {
        var mainAssembly = Assembly.GetExecutingAssembly();
        var assemblies = new List<Assembly> { mainAssembly };

        // Load plugin assemblies from the plugin directory
        if (_pluginDirectory != null && Directory.Exists(_pluginDirectory))
        {
            var pluginAssemblies = PluginAssemblyLoader.LoadPluginsFromDirectory(_pluginDirectory);
            assemblies.AddRange(pluginAssemblies);
        }

        ConventionRegistration.Register(builder, assemblies, _rulesetDataDirectory);
    }
}