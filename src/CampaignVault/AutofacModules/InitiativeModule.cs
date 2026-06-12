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
            .SingleInstance();

        builder.RegisterType<DefaultRelevantMemorySelector>().As<IRelevantMemorySelector>().SingleInstance();
        builder.RegisterType<DefaultBehavioralTensionCalculator>().As<IBehavioralTensionCalculator>().SingleInstance();
        builder.RegisterType<CampaignInitiativeSuppressionStore>().As<IInitiativeSuppressionStore>().SingleInstance();
        builder.RegisterType<NpcInitiativeService>().As<INpcInitiativeService>().SingleInstance();
    }
}
