using System;
using System.IO;
using CampaignVault.Data;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Tests;

internal static class BootstrapTestHelper
{
    public static CharacterBootstrapOrchestrator CreateOrchestrator(Random? rng = null, RaceDefinitionProvider? raceProvider = null)
    {
        var roll = new DefaultRollService(rng ?? new Random(42));
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(roll, raceProvider),
            new Pf2eRulesetResolver(roll, raceProvider),
            new NarrativeRulesetResolver(roll),
        ];
        return new CharacterBootstrapOrchestrator(new RulesetModuleSelector(modules));
    }

    public static RaceDefinitionProvider CreateRaceProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_race_bootstrap_test_" + Guid.NewGuid());
        return new RaceDefinitionProvider(dir, typeof(RaceDefinitionProvider).Assembly);
    }
}
