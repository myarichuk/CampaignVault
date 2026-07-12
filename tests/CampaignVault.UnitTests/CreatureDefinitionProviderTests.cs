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
    public void Provider_LoadsFallout2d20Creatures_FromEmbeddedResources()
    {
        var creatures = Provider.GetCreaturesForSystem(RulesetSystem.Fallout2d20);

        Assert.Equal(4, creatures.Count);
        Assert.True(creatures.ContainsKey("Rad Roach"));
        Assert.True(creatures.ContainsKey("Radroach Swarm"));
        Assert.True(creatures.ContainsKey("Ghoul"));
        Assert.True(creatures.ContainsKey("Super Mutant"));
    }

    [Fact]
    public void TryGet_RadRoach_HasExpectedStats()
    {
        var found = Provider.TryGet(RulesetSystem.Fallout2d20, "Rad Roach", out var creature);

        Assert.True(found);
        Assert.NotNull(creature);
        Assert.Equal(1, creature.Level);
        Assert.Equal(4, creature.Hp);
        Assert.Equal(1, creature.Defense);
    }

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
