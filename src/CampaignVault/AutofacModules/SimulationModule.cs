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
        builder.RegisterType<EncounterResolver>().InstancePerLifetimeScope();

        builder.RegisterBuildCallback(ctx =>
        {
            var handlers = ctx.Resolve<IEnumerable<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler>>();
            if (!handlers.Any())
            {
                throw new System.InvalidOperationException("Startup Validation Failed: No IWorldChangeHandler instances were registered in the container.");
            }
        });
    }
}
