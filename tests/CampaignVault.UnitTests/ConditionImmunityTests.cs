using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>M-24: a condition's <c>immunities</c> refuse the named conditions on a bearer.</summary>
[Collection("RavenDB")]
public class ConditionImmunityTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private static StatusChangeHandler HandlerWithWardedCondition()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv-immunity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "dnd5e", "conditions"));
        File.WriteAllText(Path.Combine(dir, "dnd5e", "conditions", "warded.yaml"),
            "name: warded\nsystem: dnd5e\ndurationType: Manual\nimmunities: [poisoned]\nmechanicalSummary: Immune to poison\n");
        return new StatusChangeHandler(new ConditionDefinitionProvider(dir, typeof(ConditionDefinitionProvider).Assembly));
    }

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

    private static Character Hero(params string[] statuses) => new()
    {
        Id = "chars/hero",
        Name = "Hero",
        SystemStats = new Dnd5eExtension { StatusEffects = [.. statuses.Select(s => new StatusEffect { Name = s, Category = "Condition" })] },
    };

    [Fact]
    public async Task ImmuneCharacter_RefusesTheCondition_AndNothingIsAdded()
    {
        using var session = fixture.Store.OpenAsyncSession();
        var hero = Hero("Warded");

        var result = await HandlerWithWardedCondition().ApplyAsync(
            new StatusChange { CharacterId = hero.Id, Status = "Poisoned" }, Context(session, hero), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Single(hero.SystemStats!.StatusEffects);
    }

    [Fact]
    public async Task CharacterWithoutTheImmunity_GetsTheCondition()
    {
        using var session = fixture.Store.OpenAsyncSession();
        var hero = Hero();

        var result = await HandlerWithWardedCondition().ApplyAsync(
            new StatusChange { CharacterId = hero.Id, Status = "Poisoned" }, Context(session, hero), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(hero.SystemStats!.StatusEffects);
    }

    [Fact]
    public async Task GrapplerWhoIsStunned_LetsGo()
    {
        using var session = fixture.Store.OpenAsyncSession();
        var ogre = Hero();
        ogre.Id = "chars/ogre";
        ogre.SystemStats!.EngagementRelations.Add(new EngagementRelation { TargetId = "chars/hero", Verb = "Grappling", Category = EngagementCategory.Physical });
        var hero = Hero("Grappled");
        hero.SystemStats!.StatusEffects[0].ConditionName = "grappled";
        hero.SystemStats.StatusEffects[0].AppliedBy = "chars/ogre";
        hero.SystemStats.EngagementRelations.Add(new EngagementRelation { TargetId = "chars/ogre", Verb = "GrappledBy", Category = EngagementCategory.Physical });
        var ctx = new ChangeContext(
            session,
            new Dictionary<string, Character> { [ogre.Id] = ogre, [hero.Id] = hero },
            new Dictionary<string, Item>(), new Dictionary<string, Location>(), new Dictionary<string, Faction>(), new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignDocumentKeys()),
            null, null, "test-campaign");

        await HandlerWithWardedCondition().ApplyAsync(
            new StatusChange { CharacterId = ogre.Id, Status = "Stunned" }, ctx, TestContext.Current.CancellationToken);

        Assert.Empty(ogre.SystemStats!.EngagementRelations);
        Assert.Empty(hero.SystemStats.EngagementRelations);
        Assert.Empty(hero.SystemStats.StatusEffects);
    }
}
