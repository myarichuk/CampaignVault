using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>character_level_up: what the next level offers, whether XP has earned it, and gaining it with the player's picks.</summary>
[Collection("RavenDB")]
public class CharacterLevelUpToolsTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private async Task<(string Slug, string Id)> Fighter(XpProgressionType progression = XpProgressionType.Standard, int xp = 3000)
    {
        var slug = "levelup-tool-" + Guid.NewGuid().ToString("N")[..8];
        var keys = new CampaignDocumentKeys();
        using var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = keys.Config(slug), ActiveSystem = RulesetSystem.Dnd5e, XpProgression = progression }, keys.Config(slug), TestContext.Current.CancellationToken);
        var id = "chars/hild-" + slug;
        await session.StoreAsync(new Character
        {
            Id = id,
            Name = "Hild",
            IsPc = true,
            CampaignName = slug,
            MaxHp = 28,
            CurrentHp = 28,
            ExperiencePoints = xp,
            ClassLevel = "Human Fighter 3",
            SystemStats = new Dnd5eExtension { Level = 3, Constitution = 15, Strength = 16, HitDie = "d10" },
        }, id, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (slug, id);
    }

    [Fact]
    public async Task Options_SayTheLevelIsEarnedByXp_AndListItsChoices()
    {
        var (slug, id) = await Fighter(xp: 3000);
        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);

        var options = await tool.CharacterLevelUp("options", id, slug);

        Assert.True(options.Success, options.Summary);
        Assert.True(options.Data!.Status!.Ready);
        Assert.Equal((3, 4, 2700), (options.Data.Status.Level, options.Data.Status.TargetLevel, options.Data.Status.XpNeeded));
        var slot = Assert.Single(options.Data.Slots);
        Assert.Equal("4.asiOrFeat", slot.Id);
        Assert.Contains("Strength", slot.Abilities!);
    }

    [Fact]
    public async Task Options_NotEarnedYet_AreStillAvailableToAsk_AndMilestoneIsNeverEarnedByXp()
    {
        var (slug, id) = await Fighter(xp: 100);
        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);
        var status = (await tool.CharacterLevelUp("options", id, slug)).Data!.Status!;
        Assert.True(status.Possible);
        Assert.False(status.Ready);

        var (milestone, mid) = await Fighter(XpProgressionType.Milestone, xp: 999999);
        var m = (await tool.CharacterLevelUp("options", mid, milestone)).Data!.Status!;
        Assert.True(m.Possible);
        Assert.False(m.Ready);
        Assert.Null(m.XpNeeded);
    }

    [Fact]
    public async Task Apply_RefusesBadOrMissingPicks_WithoutChangingAnything()
    {
        var (slug, id) = await Fighter();
        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);

        var refused = await tool.CharacterLevelUp("apply", id, slug, new Dictionary<string, List<string>> { ["4.asiOrFeat"] = ["Strength", "Strength", "Dexterity"] });
        Assert.False(refused.Success);
        Assert.Contains("you picked 3", refused.Summary);
        Assert.Equal("Human Fighter 3", (await Load(id)).ClassLevel);

        var missing = await tool.CharacterLevelUp("apply", id, slug);
        Assert.False(missing.Success);
        Assert.Contains("choose", missing.Summary);
        Assert.Equal("Human Fighter 3", (await Load(id)).ClassLevel);
    }

    [Fact]
    public async Task Apply_AnImprovement_ChangesTheScoresAndTheHitPoints()
    {
        var (slug, id) = await Fighter();
        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);

        var applied = await tool.CharacterLevelUp("apply", id, slug, new Dictionary<string, List<string>> { ["4.asiOrFeat"] = ["Constitution", "Strength"] });

        Assert.True(applied.Success, applied.Summary);
        var hild = await Load(id);
        var stats = Assert.IsType<Dnd5eExtension>(hild.SystemStats);
        Assert.Equal((16, 17, 4), (stats.Constitution, stats.Strength, stats.Level));
        Assert.Equal(28 + 6 + 3, hild.MaxHp);
        Assert.Contains(stats.LevelUpChoices, r => r.Key == "asiOrFeat" && r.Level == 4);
        Assert.Equal(4, applied.Data!.Status!.Level);
    }

    [Fact]
    public async Task ANonPlayerCharacter_CantBeLeveled()
    {
        var (slug, id) = await Fighter();
        using (var session = fixture.Store.OpenAsyncSession())
        {
            var hild = await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken);
            hild!.IsPc = false;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);
        Assert.False((await tool.CharacterLevelUp("options", id, slug)).Success);
    }

    private async Task<Character> Load(string id)
    {
        using var session = fixture.Store.OpenAsyncSession();
        return (await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken))!;
    }
}
