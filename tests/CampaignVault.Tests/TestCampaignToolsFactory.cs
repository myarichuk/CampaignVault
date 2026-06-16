using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;

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
        
        var rollSvc = rollService ?? new DefaultRollService();
        var selector = new RulesetModuleSelector([
            new Dnd5eRulesetResolver(rollSvc),
            new Pf2eRulesetResolver(rollSvc),
            new Fallout2d20RulesetResolver(rollSvc)
        ]);
        var keys = new CampaignDocumentKeys();
        var pm = pressureManager ?? new PressureManager(keys);
        orchestrator ??= new PressureOrchestrator(DefaultPressureContributors.All(), pm, selector);

        return new CampaignTools(
            repo,
            new DefaultBehaviorSynthesizer(),
            selector,
            keys,
            currentCampaign,
            pm,
            orchestrator);
    }
}