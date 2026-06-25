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
}