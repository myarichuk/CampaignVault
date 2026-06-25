using System;
using CampaignVault.Data;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Tests;

internal static class BootstrapTestHelper
{
    public static CharacterBootstrapOrchestrator CreateOrchestrator(Random? rng = null)
    {
        var roll = new DefaultRollService(rng ?? new Random(42));
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(roll),
            new Pf2eRulesetResolver(roll),
            new Fallout2d20RulesetResolver(roll),
            new NarrativeRulesetResolver(roll),
        ];
        return new CharacterBootstrapOrchestrator(new RulesetModuleSelector(modules));
    }
}
