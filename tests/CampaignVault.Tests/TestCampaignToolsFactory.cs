using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;
using System.Collections.Generic;

namespace CampaignVault.Tests;

internal static class TestCampaignToolsFactory
{
    public static CampaignTools Create(
        RavenDBFixture fixture,
        ICurrentCampaignContext? context = null,
        IPressureOrchestrator? orchestrator = null,
        IPressureManager? pressureManager = null,
        IRollService? rollService = null,
        CampaignRepository? repository = null,
        IWorldSimulationEngine? simulationEngine = null)
    {
        var currentCampaign = context ?? new CurrentCampaignContext();
        var repo = repository ?? fixture.CreateRepository(engineOverride: simulationEngine, overrides: b => {
            if (rollService != null) b.RegisterInstance(rollService).As<IRollService>();
            b.RegisterInstance(currentCampaign).As<ICurrentCampaignContext>();
        });
        
        var scope = fixture.Container.BeginLifetimeScope(b => {
            if (rollService != null) b.RegisterInstance(rollService).As<IRollService>();
            b.RegisterInstance(currentCampaign).As<ICurrentCampaignContext>();
            b.RegisterInstance(repo).As<CampaignRepository>();
            
            if (orchestrator != null) b.RegisterInstance(orchestrator).As<IPressureOrchestrator>();
            if (pressureManager != null) b.RegisterInstance(pressureManager).As<IPressureManager>();
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
