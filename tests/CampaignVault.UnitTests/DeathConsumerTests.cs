using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using CampaignVault.Services;
using CampaignVault.Data.Templates;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Death consumers: everything that must stop treating a dead character as a living one
/// (simulation rules, distress pressure) plus the cascade a death triggers (minion lapse).
/// </summary>
[Collection("RavenDB")]
public class DeathConsumerTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public DeathConsumerTests(RavenDBFixture fixture) => _fixture = fixture;

    private static Character Living(string id) => new()
    {
        Id = id, Name = id, MaxHp = 10, CurrentHp = 10, KeepAlive = true,
        Needs = new NeedsProfile(), Psychology = new PsychologyProfile(),
        SystemStats = new Dnd5eExtension(),
    };

    private static Character Dead(string id)
    {
        var c = Living(id);
        c.CurrentHp = 0;
        c.Death = new DeathRecord { Day = 2, Cause = "test" };
        return c;
    }

    private static SimulationContext Sim(params Character[] characters) => new(
        new CampaignTime { TotalDaysElapsed = 10 }, [], characters, null!, 1.0, "test_campaign");

    [Fact]
    public async Task NeedsAccumulationRule_SkipsDead_ButNotLiving()
    {
        var dead = Dead("chars/dead");
        var alive = Living("chars/alive");

        var result = await new NeedsAccumulationRule().ApplyAsync(Sim(dead, alive), TestContext.Current.CancellationToken);

        var needChanges = result.Deltas.OfType<NeedChange>().ToList();
        Assert.DoesNotContain(needChanges, d => d.CharacterId == dead.Id);
        Assert.Contains(needChanges, d => d.CharacterId == alive.Id);
    }

    [Fact]
    public async Task SurvivalDeprivationRule_SkipsDead()
    {
        var provider = new ConditionDefinitionProvider(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_death_survival_" + Guid.NewGuid()),
            typeof(ConditionDefinitionProvider).Assembly);
        var dead = Dead("chars/dead");
        dead.Needs.ActiveNeeds["hunger"] = 95f;
        dead.Needs.ActiveNeeds["thirst"] = 95f;
        if (dead.SystemStats is Dnd5eExtension stats)
        {
            stats.Attributes["deprivation_streak_hunger"] = 5f;
        }

        var result = await new SurvivalDeprivationRule(provider).ApplyAsync(Sim(dead), TestContext.Current.CancellationToken);

        Assert.Empty(result.Deltas);
        Assert.Empty(result.NarrativeEvents);
    }

    [Fact]
    public async Task Death_LapsesBoundMinions_AndRecordsTheLapse()
    {
        var caster = Living("chars/caster");
        var minion = Living("chars/minion");
        minion.ControlledById = caster.Id;
        minion.MinionBinding = new MinionBinding
        {
            ControllerId = caster.Id,
            SpellName = "animate_dead",
            Disposition = SummonDisposition.Hostile,
            DurationRounds = 600,
        };
        caster.ControlsMinionIds.Add(minion.Id);
        var summary = new List<string>();
        var ctx = ChangeContextTestHelper.Create(
            characters: new() { [caster.Id] = caster, [minion.Id] = minion }, summary: summary);

        var result = await new DeathChangeHandler().ApplyAsync(
            new DeathChange { CharacterId = caster.Id }, ctx, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(minion.MinionBinding!.ControlLapsed);
        Assert.Empty(caster.ControlsMinionIds);
    }

    [Fact]
    public async Task DistressContributors_IgnoreDead_ButStillFlagWoundedLiving()
    {
        var campaign = "death-distress-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var deadId = $"chars/{campaign}-dead";
        var woundedId = $"chars/{campaign}-wounded";

        using var session = _fixture.Store.OpenAsyncSession();
        var cs = _fixture.CreateCampaignSession(session, campaign);
        foreach (var (id, hp) in new[] { (deadId, 0), (woundedId, 1) })
        {
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = id, Name = id, KeepAlive = true, IsPc = true, MaxHp = 20, CurrentHp = hp,
                SystemStats = new Dnd5eExtension { ArmorClass = 12 },
            });
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A dead PC keeps KeepAlive (only NPCs lose it on death), so it is exactly the case the
        // contributors must filter themselves.
        var deadDoc = await session.LoadAsync<Character>(deadId, TestContext.Current.CancellationToken);
        deadDoc!.Death = new DeathRecord { Day = 1, Cause = "test" };
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var waitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - waitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => !x.IsStale)) break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var ctx = new PressureContext(
            campaign, await repo.GetTimeAsync(cs), await repo.GetCampaignConfigAsync(cs), session);
        var items = (await new CharacterDistressPressureContributor().EvaluateAsync(ctx, TestContext.Current.CancellationToken))
            .Concat(await new PressureHintEnricher().EvaluateAsync(ctx, TestContext.Current.CancellationToken))
            .ToList();

        Assert.DoesNotContain(items, p => p.EntityId == deadId);
        Assert.Contains(items, p => p.EntityId == woundedId
            && p.GroupingKey == CharacterDistressPressureContributor.CriticallyWoundedGroupingKey);
    }
}

/// <summary>5e dying rules for PCs: death saves, damage at 0 HP, massive damage.</summary>
public class DeathSaveTests
{
    private static Character Pc(int hp = 20, int max = 20) => new()
    {
        Id = "chars/maeve", Name = "Maeve", MaxHp = max, CurrentHp = hp, IsPc = true,
        SystemStats = new Dnd5eExtension(), CurrentLocationId = "locations/gorse",
    };

    private static ChangeContext Ctx(Character c) =>
        ChangeContextTestHelper.Create(characters: new() { [c.Id] = c }, summary: []);

    private static Task<ChangeHandlerResult> Hp(Character c, int delta) =>
        new HpChangeHandler(NSubstitute.Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = c.Id, Delta = delta }, Ctx(c), TestContext.Current.CancellationToken);

    private static Task<ChangeHandlerResult> Save(Character c, int? roll) =>
        new DeathSaveChangeHandler(NSubstitute.Substitute.For<IRollService>())
            .ApplyAsync(new DeathSaveChange { CharacterId = c.Id, Roll = roll }, Ctx(c), TestContext.Current.CancellationToken);

    [Fact]
    public void DeathSaveChange_RoundTripsThroughPolymorphicJson()
    {
        var change = System.Text.Json.JsonSerializer.Deserialize<WorldChange>(
            """{"$type":"death_save","characterId":"chars/maeve","roll":14}""");
        var save = Assert.IsType<DeathSaveChange>(change);
        Assert.Equal(14, save.Roll);
    }

    [Fact]
    public async Task DamageToZero_StartsTally_ButDoesNotKill()
    {
        var pc = Pc(hp: 5);
        await Hp(pc, -8);

        Assert.Equal(0, pc.CurrentHp);
        Assert.False(pc.IsDead);
        Assert.NotNull(pc.DeathSaves);
    }

    [Fact]
    public async Task MassiveDamage_KillsPcOutright()
    {
        var pc = Pc(hp: 5, max: 20);
        await Hp(pc, -25); // 5 to reach 0, 20 left over = max HP

        Assert.True(pc.IsDead);
        Assert.Equal("massive damage", pc.Death!.Cause);
    }

    [Fact]
    public async Task DamageAtZero_AddsFailure_ThirdKills()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();

        await Hp(pc, -3);
        await Hp(pc, -3);
        Assert.Equal(2, pc.DeathSaves!.Failures);
        Assert.False(pc.IsDead);

        await Hp(pc, -3);
        Assert.True(pc.IsDead);
    }

    [Fact]
    public async Task Healing_ClearsTally()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally { Failures = 2 };

        await Hp(pc, 4);

        Assert.Null(pc.DeathSaves);
        Assert.Equal(4, pc.CurrentHp);
    }

    [Fact]
    public async Task Npc_AtZero_NeverAutoDies()
    {
        var npc = Pc(hp: 5);
        npc.IsPc = false;
        await Hp(npc, -50);

        Assert.False(npc.IsDead);
        Assert.Null(npc.DeathSaves);
    }

    [Fact]
    public async Task Save_SuccessesStabilise()
    {
        var pc = Pc(hp: 0);
        await Save(pc, 12);
        await Save(pc, 10);
        await Save(pc, 15);

        Assert.True(pc.DeathSaves!.Stable);
        Assert.False(pc.IsDead);
    }

    [Fact]
    public async Task Save_NatOne_CountsTwoFailures_ThenDies()
    {
        var pc = Pc(hp: 0);
        await Save(pc, 1);
        Assert.Equal(2, pc.DeathSaves!.Failures);

        await Save(pc, 5);
        Assert.True(pc.IsDead);
    }

    [Fact]
    public async Task Save_NatTwenty_RestoresOneHp()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally { Failures = 2 };
        await Save(pc, 20);

        Assert.Equal(1, pc.CurrentHp);
        Assert.Null(pc.DeathSaves);
    }

    [Fact]
    public async Task Save_RejectsConsciousNpcAndBadRoll()
    {
        var awake = Pc(hp: 5);
        Assert.False((await Save(awake, 12)).Success);

        var npc = Pc(hp: 0);
        npc.IsPc = false;
        Assert.False((await Save(npc, 12)).Success);

        Assert.False((await Save(Pc(hp: 0), 25)).Success);
    }
}
