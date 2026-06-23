using System.Reflection;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Scenes;
using ModelContextProtocol.Server;

namespace CampaignVault.AutofacModules;

public class CampaignCoreModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignDocumentKeys>().SingleInstance();
        builder.RegisterType<CampaignSelectionStore>().SingleInstance();
        builder.RegisterType<HttpMcpSessionAccessor>().As<IMcpSessionAccessor>().InstancePerLifetimeScope();
        builder.RegisterType<SessionKeyedCurrentCampaignContext>().As<ICurrentCampaignContext>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignVault.Data.ChangeHandlers.WorldChangeDispatcher>().InstancePerLifetimeScope();
        builder.RegisterType<SceneAssembler>().InstancePerLifetimeScope();
        builder.RegisterType<SceneNpcMerger>().InstancePerLifetimeScope();
        builder.RegisterType<SceneFactionSummaryFactory>().InstancePerLifetimeScope();

        builder.RegisterType<CampaignRepository>().InstancePerLifetimeScope();

        var assembly = typeof(CampaignCoreModule).Assembly;
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .InstancePerLifetimeScope();
    }
}