using System;
using System.IO;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class CreatureDefinitionProviderTests
{
    private static readonly CreatureDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_creaturedef_test_" + Guid.NewGuid()),
        typeof(CreatureDefinitionProvider).Assembly);

    [Fact]
    public void GetCreaturesForSystem_Dnd5e_ReturnsSeedCreatures()
    {
        var creatures = Provider.GetCreaturesForSystem(RulesetSystem.Dnd5e);

        Assert.NotEmpty(creatures);
        Assert.Contains(creatures.Keys, k => k == "Goblin");
        Assert.Contains(creatures.Keys, k => k == "Skeleton");
        Assert.True(creatures["Goblin"].ChallengeRating == "1/4");
    }

    [Fact]
    public void GetCreaturesForSystem_Pf2e_ReturnsSeedCreatures()
    {
        var creatures = Provider.GetCreaturesForSystem(RulesetSystem.Pathfinder2e);

        Assert.NotEmpty(creatures);
        Assert.Contains(creatures.Keys, k => k == "Goblin Warrior");
        Assert.Contains(creatures.Keys, k => k == "Wolf");
    }
}
