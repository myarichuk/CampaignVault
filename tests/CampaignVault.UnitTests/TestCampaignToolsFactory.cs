using System.Reflection;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Tools;

namespace CampaignVault.Tests;

internal static class TestCampaignToolsFactory
{
    public static CampaignTools Create(
        RavenDBFixture fixture,
        IPressureOrchestrator? orchestrator = null,
        IPressureManager? pressureManager = null,
        IRollService? rollService = null,
        CampaignRepository? repository = null,
        IWorldSimulationEngine? simulationEngine = null)
    {
        var repo = repository ?? fixture.CreateRepository(engineOverride: simulationEngine, overrides: b =>
        {
            if (rollService != null) b.RegisterInstance(rollService).As<IRollService>();
        });

        var scope = fixture.Container.BeginLifetimeScope(b =>
        {
            if (rollService != null) b.RegisterInstance(rollService).As<IRollService>();
            b.RegisterInstance(repo).As<CampaignRepository>();

            if (orchestrator != null) b.RegisterInstance(orchestrator).As<IPressureOrchestrator>();
            if (pressureManager != null) b.RegisterInstance(pressureManager).As<IPressureManager>();

            b.RegisterAssemblyTypes(typeof(ExplorationTools).Assembly)
                .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() != null)
                .InstancePerLifetimeScope();
        });

        return new CampaignTools(
            scope.Resolve<ExplorationTools>(),
            scope.Resolve<MutationTools>(),
            scope.Resolve<DeepDiveTools>(),
            scope.Resolve<WorldBuilderTools>(),
            scope.Resolve<CombatTools>(),
            scope.Resolve<CampaignManagementTools>(),
            scope.Resolve<MetaTools>());
    }

    /// <summary>
    /// Resolves WorldBuilderTools directly for tests that exercise tools not wrapped by the
    /// legacy CampaignTools facade (e.g. upsert_quest, upsert_faction, upsert_plot_thread).
    /// </summary>
    public static WorldBuilderTools CreateWorldBuilderTools(RavenDBFixture fixture, CampaignRepository? repository = null)
    {
        var repo = repository ?? fixture.CreateRepository();

        var scope = fixture.Container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(repo).As<CampaignRepository>();
            b.RegisterAssemblyTypes(typeof(ExplorationTools).Assembly)
                .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() != null)
                .InstancePerLifetimeScope();
        });

        return scope.Resolve<WorldBuilderTools>();
    }

    /// <summary>
    /// Resolves DeepDiveTools directly for tests that exercise tools not wrapped by the
    /// legacy CampaignTools facade (e.g. get_entity).
    /// </summary>
    public static DeepDiveTools CreateDeepDiveTools(RavenDBFixture fixture, CampaignRepository? repository = null) =>
        CreateTool<DeepDiveTools>(fixture, repository);

    /// <summary>
    /// Resolves any tool class directly for tests exercising tools outside the legacy facade
    /// (SessionTools.start_session, CombatTools.combat, CampaignManagementTools.get_rules_reference, ...).
    /// </summary>
    public static T CreateTool<T>(RavenDBFixture fixture, CampaignRepository? repository = null) where T : notnull
    {
        var repo = repository ?? fixture.CreateRepository();

        var scope = fixture.Container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(repo).As<CampaignRepository>();
            b.RegisterAssemblyTypes(typeof(ExplorationTools).Assembly)
                .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() != null)
                .InstancePerLifetimeScope();
        });

        return scope.Resolve<T>();
    }
}