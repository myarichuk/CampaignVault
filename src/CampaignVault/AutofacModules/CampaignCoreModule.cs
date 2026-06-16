using Autofac;
using CampaignVault.Data;
using ModelContextProtocol.Server;
using System.Reflection;

namespace CampaignVault.AutofacModules;

public class CampaignCoreModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignDocumentKeys>().SingleInstance();
        builder.RegisterType<CurrentCampaignContext>().As<ICurrentCampaignContext>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignRepository>().InstancePerLifetimeScope();

        var assembly = typeof(CampaignCoreModule).Assembly;
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .InstancePerLifetimeScope();
    }
}
