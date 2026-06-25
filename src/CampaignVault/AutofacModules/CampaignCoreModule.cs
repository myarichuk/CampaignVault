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
        builder.RegisterType<CampaignVault.Data.ChangeHandlers.WorldChangeDispatcher>().InstancePerLifetimeScope();
        builder.RegisterType<SceneAssembler>().InstancePerLifetimeScope();
        builder.RegisterType<SceneNpcMerger>().InstancePerLifetimeScope();
        builder.RegisterType<SceneFactionSummaryFactory>().InstancePerLifetimeScope();

        builder.RegisterType<CampaignRepository>().InstancePerLifetimeScope();

    }
}