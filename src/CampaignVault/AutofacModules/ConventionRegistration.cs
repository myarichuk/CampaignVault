using System.Reflection;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Scenes;
using CampaignVault.Data.Templates;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Tools;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Central convention-based Autofac registration for the CampaignVault assembly.
/// </summary>
internal static class ConventionRegistration
{
    private static readonly string[] NameMatchedNamespaces =
    [
        typeof(PressureManager).Namespace!,
        typeof(PressureOrchestrator).Namespace!,
        typeof(NpcInitiativeService).Namespace!,
        typeof(RulesetModuleSelector).Namespace!,
        typeof(CharacterBootstrapOrchestrator).Namespace!,
    ];

    private static readonly string[] NameMatchedSuffixes =
        ["Manager", "Orchestrator", "Selector", "Store", "Service"];

    public static void Register(ContainerBuilder builder, Assembly assembly, string rulesetDataDirectory)
    {
        RegisterMarkerCollections(builder, assembly);
        RegisterRulesetData(builder, assembly, rulesetDataDirectory);
        RegisterDefaultImplementations(builder, assembly);
        RegisterNameMatchedServices(builder, assembly);
        RegisterApplicationCore(builder);
        RegisterStartupValidation(builder);
    }

    private static void RegisterMarkerCollections(ContainerBuilder builder, Assembly assembly)
    {
        RegisterCollection<ISimulationRule>(builder, assembly);
        RegisterCollection<IPressureContributor>(builder, assembly);
        RegisterCollection<INpcInitiativeSignalProvider>(builder, assembly);
        RegisterCollection<IRulesetModule>(builder, assembly);

        // Register tools explicitly to ensure dependency order: ExplorationTools before DeepDiveTools
        builder.RegisterType<ExplorationTools>()
            .AsSelf()
            .As<IMcpServerTool>()
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IMcpServerTool>() && !t.IsAbstract && t.Name != nameof(ExplorationTools))
            .As<IMcpServerTool>()
            .InstancePerLifetimeScope();

        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IWorldChangeHandler>() && !t.IsAbstract)
            .AsSelf()
            .As<IWorldChangeHandler>()
            .InstancePerLifetimeScope();
    }

    private static void RegisterCollection<TService>(ContainerBuilder builder, Assembly assembly)
        where TService : class
    {
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<TService>() && !t.IsAbstract)
            .As<TService>()
            .InstancePerLifetimeScope();
    }

    private static void RegisterRulesetData(
        ContainerBuilder builder,
        Assembly assembly,
        string rulesetDataDirectory)
    {
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IRulesetYamlProvider>() && !t.IsAbstract)
            .AsSelf()
            .WithParameter(
                (pi, _) => pi.ParameterType == typeof(string) && pi.Name == "rulesetDataDirectory",
                (_, _) => rulesetDataDirectory)
            .WithParameter(
                (pi, _) => pi.ParameterType == typeof(Assembly) && pi.Name == "embeddedAssembly",
                (_, _) => assembly)
            .SingleInstance();

        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.IsAssignableTo<IRulesetDataInitializer>() && !t.IsAbstract)
            .AsSelf()
            .InstancePerLifetimeScope();
    }

    private static void RegisterDefaultImplementations(ContainerBuilder builder, Assembly assembly)
    {
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => t.Namespace?.StartsWith("CampaignVault", StringComparison.Ordinal) == true)
            .Where(t => t.Name.StartsWith("Default", StringComparison.Ordinal) && !t.IsAbstract)
            .Where(t => t.Name != nameof(DefaultRollService))
            .Where(t => GetCampaignVaultServiceInterfaces(t).Length > 0)
            .As(t => GetCampaignVaultServiceInterfaces(t))
            .InstancePerLifetimeScope();

        builder.RegisterType<DefaultRollService>()
            .As<IRollService>()
            .SingleInstance();
    }

    private static void RegisterNameMatchedServices(ContainerBuilder builder, Assembly assembly)
    {
        builder.RegisterAssemblyTypes(assembly)
            .Where(t => !t.IsAbstract && t.IsClass)
            .Where(t => t.Namespace != null && NameMatchedNamespaces.Contains(t.Namespace))
            .Where(t => NameMatchedSuffixes.Any(suffix => t.Name.EndsWith(suffix, StringComparison.Ordinal)))
            .Where(t => GetCampaignVaultServiceInterfaces(t).Length > 0)
            .As(t => GetCampaignVaultServiceInterfaces(t))
            .InstancePerLifetimeScope();
    }

    private static void RegisterApplicationCore(ContainerBuilder builder)
    {
        builder.RegisterType<CampaignDocumentKeys>().SingleInstance();
        builder.RegisterType<WorldChangeDispatcher>().InstancePerLifetimeScope();
        builder.RegisterType<SceneAssembler>().InstancePerLifetimeScope();
        builder.RegisterType<SceneNpcMerger>().InstancePerLifetimeScope();
        builder.RegisterType<SceneFactionSummaryFactory>().InstancePerLifetimeScope();
        builder.RegisterType<CampaignRepository>().InstancePerLifetimeScope();
        builder.RegisterType<EncounterResolver>().InstancePerLifetimeScope();
        builder.RegisterType<CharacterBootstrapOrchestrator>().InstancePerLifetimeScope();
    }

    private static void RegisterStartupValidation(ContainerBuilder builder)
    {
        builder.RegisterBuildCallback(ctx =>
        {
            var handlers = ctx.Resolve<IEnumerable<IWorldChangeHandler>>();
            if (!handlers.Any())
            {
                throw new InvalidOperationException(
                    "Startup validation failed: no IWorldChangeHandler instances were registered.");
            }
        });
    }

    private static Type[] GetCampaignVaultServiceInterfaces(Type type) =>
        type.GetInterfaces()
            .Where(i => i.Namespace?.StartsWith("CampaignVault", StringComparison.Ordinal) == true
                        && i.Name.StartsWith('I'))
            .ToArray();
}