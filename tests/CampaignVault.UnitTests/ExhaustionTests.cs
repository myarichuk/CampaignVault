using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Contributors;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>M-25: one exhaustion (the "Exhaustion N" status) with teeth: a lowered maximum at 4 and death at 6.</summary>
[Collection("RavenDB")]
public class ExhaustionTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private static readonly ConditionDefinitionProvider Conditions = RulesetDataTestHelper.CreateConditionProvider();

    private ChangeContext Context(IAsyncDocumentSession session, Character character) => new(
        session,
        new Dictionary<string, Character> { [character.Id] = character },
        new Dictionary<string, Item>(),
        new Dictionary<string, Location>(),
        new Dictionary<string, Faction>(),
        new Dictionary<string, Quest>(),
        NullLogger.Instance,
        () => Task.FromResult(new CampaignTime { TotalDaysElapsed = 10 }),
        () => Task.FromResult(new Dictionary<string, string>()),
        _ => Task.CompletedTask,
        [],
        new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignDocumentKeys()),
        null,
        null,
        "test-campaign");

    private static Character Hero(int maxHp = 40) => new()
    {
        Id = "chars/hero",
        Name = "Hero",
        MaxHp = maxHp,
        CurrentHp = maxHp,
        SystemStats = new Dnd5eExtension(),
    };

    private Task<ChangeHandlerResult> Apply(Character hero, string status)
    {
        using var session = fixture.Store.OpenAsyncSession();
        return new StatusChangeHandler(Conditions).ApplyAsync(
            new StatusChange { CharacterId = hero.Id, Effect = new StatusEffect { Name = status, Category = "Condition", ConditionName = "exhaustion" } },
            Context(session, hero), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LevelSix_Kills_WithTheCauseExhaustion()
    {
        var hero = Hero();

        var result = await Apply(hero, "Exhaustion 6");

        Assert.True(result.Success);
        Assert.True(hero.IsDead);
        Assert.Equal("exhaustion", hero.Death!.Cause);
    }

    [Fact]
    public async Task LevelFive_DoesNotKill_ButLevelFourHalvesTheMaximum()
    {
        var hero = Hero();

        await Apply(hero, "Exhaustion 4");
        Assert.False(hero.IsDead);
        Assert.Equal(20, hero.CurrentHp);
        Assert.Equal(40, hero.MaxHp);   // stored maximum untouched
        Assert.Equal(20, EffectiveMax(hero));

        await Apply(hero, "Exhaustion 5");
        Assert.False(hero.IsDead);
    }

    private static int EffectiveMax(Character c) =>
        CampaignVault.Rulesets.ConditionMechanics.EffectiveMaxHp(c, Conditions);

    [Fact]
    public async Task Healing_IsCappedAtTheEffectiveMaximum_AndRestoredWhenTheConditionGoes()
    {
        var hero = Hero();
        hero.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Exhaustion 4", Category = "Condition", ConditionName = "exhaustion" });
        hero.CurrentHp = 10;
        using var session = fixture.Store.OpenAsyncSession();
        var handler = new HpChangeHandler(Substitute.For<IRollService>(), Conditions);

        await handler.ApplyAsync(new HpChange { CharacterId = hero.Id, Delta = 100 }, Context(session, hero), TestContext.Current.CancellationToken);
        Assert.Equal(20, hero.CurrentHp);

        hero.SystemStats.StatusEffects.Clear();
        await handler.ApplyAsync(new HpChange { CharacterId = hero.Id, Delta = 100 }, Context(session, hero), TestContext.Current.CancellationToken);
        Assert.Equal(40, hero.CurrentHp);
    }

    [Fact]
    public void TheTableEndsAtSix()
    {
        Assert.Equal(6, Conditions.GetConditionsForSystem("dnd5e")["exhaustion"].MaxLevel);
    }

    [Fact]
    public void Contributor_ReadsTheStatus_AndFallsBackToTheAttribute()
    {
        var viaStatus = Hero();
        viaStatus.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Exhaustion 3", Category = "Condition", ConditionName = "exhaustion" });
        var viaAttribute = Hero();
        viaAttribute.SystemStats!.Attributes["exhaustion_level"] = 4f;

        Assert.Equal(3, Dnd5eExhaustionPressureContributor.Level(viaStatus));
        Assert.Equal(4, Dnd5eExhaustionPressureContributor.Level(viaAttribute));
        Assert.Equal(0, Dnd5eExhaustionPressureContributor.Level(Hero()));
    }
}
