using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class LevelUpChoiceApplicationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public LevelUpChoiceApplicationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LevelUp_WithChoices_AppendsToLevelUpChoiceHistory()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        const string campaign = "level-up-choices";

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var fighter = new Character
        {
            Id = "chars/fighter",
            Name = "Fighter",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 20,
            CurrentHp = 20,
            ClassLevel = "Fighter 2",
            SystemStats = new Dnd5eExtension { Constitution = 14, HitDie = "d10", Level = 2 },
        };
        await session.StoreAsync(fighter);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateLevelUpHandler(keys);
        var ctx = CreateContext(session, campaign);
        ctx.RegisterNewCharacter(fighter);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/fighter",
            LevelsGained = 1,
            Choices = new Dictionary<string, string> { ["subclass"] = "battleMaster" },
        }, ctx);

        Assert.True(result.Success);
        var entry = Assert.Single(fighter.SystemStats!.LevelUpChoices);
        Assert.Equal("subclass", entry.Key);
        Assert.Equal("battleMaster", entry.Value);
        Assert.Equal(3, entry.Level);
    }

    [Fact]
    public async Task LevelUp_RepeatedChoiceKeyAcrossLevels_DoesNotOverwritePreviousEntry()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        const string campaign = "level-up-repeat-choices";

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var fighter = new Character
        {
            Id = "chars/fighter2",
            Name = "Fighter",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 30,
            CurrentHp = 30,
            ClassLevel = "Fighter 3",
            SystemStats = new Dnd5eExtension { Constitution = 14, HitDie = "d10", Level = 3 },
        };
        await session.StoreAsync(fighter);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateLevelUpHandler(keys);
        var ctx = CreateContext(session, campaign);
        ctx.RegisterNewCharacter(fighter);

        await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/fighter2",
            LevelsGained = 1,
            Choices = new Dictionary<string, string> { ["asiOrFeat"] = "greatWeaponMaster" },
        }, ctx);

        await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/fighter2",
            LevelsGained = 1,
            Choices = new Dictionary<string, string> { ["asiOrFeat"] = "sentinel" },
        }, ctx);

        Assert.Equal(2, fighter.SystemStats!.LevelUpChoices.Count);
        Assert.Equal("greatWeaponMaster", fighter.SystemStats.LevelUpChoices[0].Value);
        Assert.Equal(4, fighter.SystemStats.LevelUpChoices[0].Level);
        Assert.Equal("sentinel", fighter.SystemStats.LevelUpChoices[1].Value);
        Assert.Equal(5, fighter.SystemStats.LevelUpChoices[1].Level);
    }

    [Fact]
    public async Task LevelUp_WithAbilityScoreIncreases_AppliesToDnd5eAbilityScores()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        const string campaign = "level-up-asi";

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var fighter = new Character
        {
            Id = "chars/fighter3",
            Name = "Fighter",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 40,
            CurrentHp = 40,
            ClassLevel = "Fighter 3",
            SystemStats = new Dnd5eExtension { Strength = 16, Dexterity = 12, Constitution = 14, HitDie = "d10", Level = 3 },
        };
        await session.StoreAsync(fighter);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateLevelUpHandler(keys);
        var ctx = CreateContext(session, campaign);
        ctx.RegisterNewCharacter(fighter);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/fighter3",
            LevelsGained = 1,
            AbilityScoreIncreases = new Dictionary<string, int> { ["Strength"] = 2 },
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal(18, ((Dnd5eExtension)fighter.SystemStats!).Strength);
        Assert.Equal(12, ((Dnd5eExtension)fighter.SystemStats).Dexterity);
    }

    private static ChangeContext CreateContext(Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaign)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            null,
            campaign);
    }
}
