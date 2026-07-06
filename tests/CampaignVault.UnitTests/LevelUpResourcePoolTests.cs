using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class LevelUpResourcePoolTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public LevelUpResourcePoolTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LevelUp_Wizard_GainsNewSlotTierAndPreservesSpentSlots()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        const string campaign = "level-up-wizard";

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var wizard = new Character
        {
            Id = "chars/wizard",
            Name = "Wizard",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 24,
            CurrentHp = 24,
            ClassLevel = "Wizard 4",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 10,
                HitDie = "d6",
                Level = 4,
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 1, Max = 4, Recovery = RecoveryType.LongRest },
                    ["spell_slots_2"] = new() { Current = 2, Max = 3, Recovery = RecoveryType.LongRest },
                }
            }
        };
        await session.StoreAsync(wizard);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateLevelUpHandler(keys);
        var ctx = CreateContext(session, campaign);
        ctx.RegisterNewCharacter(wizard);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/wizard",
            LevelsGained = 1,
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal(5, ((Dnd5eExtension)wizard.SystemStats).Level);
        Assert.Equal(1, wizard.SystemStats.ResourcePools["spell_slots_1"].Current);
        Assert.Equal(4, wizard.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(2, wizard.SystemStats.ResourcePools["spell_slots_2"].Current);
        Assert.True(wizard.SystemStats.ResourcePools.ContainsKey("spell_slots_3"));
        Assert.Equal(2, wizard.SystemStats.ResourcePools["spell_slots_3"].Max);
        Assert.Equal(2, wizard.SystemStats.ResourcePools["spell_slots_3"].Current);
    }

    [Fact]
    public async Task LevelUp_MulticlassWizard_IncreasesCasterLevelPools()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        const string campaign = "level-up-multiclass";

        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var gish = new Character
        {
            Id = "chars/gish",
            Name = "Gish",
            IsPc = true,
            CampaignName = campaign,
            MaxHp = 44,
            CurrentHp = 44,
            ClassLevel = "Fighter 5 / Wizard 3",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 14,
                HitDie = "d10",
                Level = 8,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Fighter", Level = 5 },
                    new ClassLevelEntry { Class = "Wizard", Level = 3 },
                ],
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 0, Max = 4, Recovery = RecoveryType.LongRest },
                    ["spell_slots_2"] = new() { Current = 1, Max = 2, Recovery = RecoveryType.LongRest },
                    ["action_surge"] = new() { Current = 0, Max = 1, Recovery = RecoveryType.ShortRest },
                }
            }
        };
        await session.StoreAsync(gish);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateLevelUpHandler(keys);
        var ctx = CreateContext(session, campaign);
        ctx.RegisterNewCharacter(gish);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/gish",
            LevelsGained = 1,
            ClassGained = "Wizard",
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal("Fighter 5 / Wizard 4", gish.ClassLevel);
        Assert.Equal(0, gish.SystemStats.ResourcePools["spell_slots_1"].Current);
        Assert.Equal(4, gish.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(3, gish.SystemStats.ResourcePools["spell_slots_2"].Max);
        Assert.Equal(1, gish.SystemStats.ResourcePools["spell_slots_2"].Current);
        Assert.Equal(1, gish.SystemStats.ResourcePools["action_surge"].Max);
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