using System.Reflection;
using Autofac;
using CampaignVault.Data.Templates;
using CampaignVault.Services;

namespace CampaignVault.AutofacModules;

public class TemplatesModule : Autofac.Module
{
    private readonly string _rulesetDataDirectory;

    public TemplatesModule(string? rulesetDataDirectory = null)
    {
        _rulesetDataDirectory = rulesetDataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "RulesetData");
    }

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RulesetTemplateRegistry>().SingleInstance();

        var assembly = Assembly.GetExecutingAssembly();
        var dir = _rulesetDataDirectory;
        builder.Register(_ => new ResourcePoolProvider(dir, assembly))
            .SingleInstance();
        builder.Register(_ => new ClassDefinitionProvider(dir, assembly))
            .SingleInstance();
    }
}
