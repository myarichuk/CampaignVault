using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;

namespace CampaignVault.Tests;

internal static class TestCampaignToolsFactory
{
    public static CampaignTools Create(
        IDocumentStore store,
        ICurrentCampaignContext? context = null,
        IPressureOrchestrator? orchestrator = null,
        IPressureManager? pressureManager = null)
    {
        var repo = new CampaignRepository(store);
        var rollSvc = new DefaultRollService();
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
            context ?? new CurrentCampaignContext(),
            pm,
            orchestrator);
    }
}