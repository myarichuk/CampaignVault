using Autofac;
using CampaignVault.Data;

namespace CampaignVault.AutofacModules;

public class SimulationModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        var assembly = typeof(Program).Assembly;

        // Simulation Rules
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<ISimulationRule>() && !t.IsAbstract)
            .As<ISimulationRule>()
            .InstancePerLifetimeScope();

        // World Change Handlers
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler>() && !t.IsAbstract)
            .As<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler>()
            .InstancePerLifetimeScope();

        builder.RegisterType<DefaultSimulationEngine>().As<IWorldSimulationEngine>().InstancePerLifetimeScope();
        builder.RegisterType<DefaultBehaviorSynthesizer>().As<INpcBehaviorSynthesizer>().InstancePerLifetimeScope();
    }
}
