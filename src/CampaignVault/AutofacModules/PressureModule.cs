using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;

namespace CampaignVault.AutofacModules;

public class PressureModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        var assembly = typeof(Program).Assembly;

        // Pressure Contributors
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IPressureContributor>() && !t.IsAbstract)
            .As<IPressureContributor>()
            .InstancePerLifetimeScope();

        builder.RegisterType<PressureOrchestrator>().As<IPressureOrchestrator>().InstancePerLifetimeScope();
        builder.RegisterType<PressureManager>().As<IPressureManager>().InstancePerLifetimeScope();
    }
}
