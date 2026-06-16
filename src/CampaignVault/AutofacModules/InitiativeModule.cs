using Autofac;
using CampaignVault.Data.Initiative;

namespace CampaignVault.AutofacModules;

public class InitiativeModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        var assembly = typeof(Program).Assembly;

        // NPC Initiative Signal Providers
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<INpcInitiativeSignalProvider>() && !t.IsAbstract)
            .As<INpcInitiativeSignalProvider>()
            .InstancePerLifetimeScope();

        builder.RegisterType<DefaultRelevantMemorySelector>().As<IRelevantMemorySelector>().InstancePerLifetimeScope();
        builder.RegisterType<DefaultBehavioralTensionCalculator>().As<IBehavioralTensionCalculator>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignInitiativeSuppressionStore>().As<IInitiativeSuppressionStore>().InstancePerLifetimeScope();
        builder.RegisterType<NpcInitiativeService>().As<INpcInitiativeService>().InstancePerLifetimeScope();
    }
}
