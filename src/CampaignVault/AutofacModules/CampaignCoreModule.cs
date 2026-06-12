using Autofac;
using CampaignVault.Data;

namespace CampaignVault.AutofacModules;

public class CampaignCoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignDocumentKeys>().SingleInstance();
        builder.RegisterType<CurrentCampaignContext>().As<ICurrentCampaignContext>().SingleInstance();
        builder.RegisterType<CampaignRepository>().SingleInstance();
    }
}
