using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 3: control lapses — concentration broken, caster downed, duration elapsed.
/// </summary>
[Collection("RavenDB")]
public class MinionLifecycleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public MinionLifecycleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Character ConcentratingCaster() => new()
    {
        Id = "chars/caster",
        Name = "Caster",
        MaxHp = 50,
        CurrentHp = 50,
        SystemStats = new Dnd5eExtension
        {
            Constitution = 10,
            StatusEffects = [new StatusEffect { Name = "Concentration: Conjure Elemental", Category = "Buff" }],
        },
    };

    private static Character BoundMinion(string id, string name, bool concentrationBound, SummonDisposition disposition) => new()
    {
        Id = id,
        Name = name,
        MaxHp = 20,
        CurrentHp = 20,
        ControlledById = "chars/caster",
        MinionBinding = new MinionBinding
        {
            ControllerId = "chars/caster",
            SpellName = "conjure_elemental",
            Disposition = disposition,
            ConcentrationBound = concentrationBound,
            DurationRounds = 600,
        },
        SystemStats = new Dnd5eExtension(),
    };

    private ChangeContext CreateHpContext(IAsyncDocumentSession session, Dictionary<string, Character> characters) =>
        new(
            session,
            characters,
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
            "lifecycle-test");

    private sealed class FixedRollService(int result) : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RollOutcome { Tag = request.Tag, Result = result + request.Bonus });

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
            IEnumerable<RollRequest> requests, CancellationToken ct = default)
        {
            var outcomes = new List<RollOutcome>();
            foreach (var request in requests)
            {
                outcomes.Add(await RollAsync(request, ct));
            }

            return outcomes;
        }
    }

    [Fact]
    public async Task HpChange_BrokenConcentration_LapsesOnlyConcentrationBound()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var caster = ConcentratingCaster();
        var bound = BoundMinion("chars/bound", "Bound", concentrationBound: true, SummonDisposition.Hostile);
        var plain = BoundMinion("chars/plain", "Plain", concentrationBound: false, SummonDisposition.Loyal);
        caster.ControlsMinionIds.Add(bound.Id);
        caster.ControlsMinionIds.Add(plain.Id);
        var context = CreateHpContext(session, new Dictionary<string, Character>
        {
            [caster.Id] = caster,
            [bound.Id] = bound,
            [plain.Id] = plain,
        });

        var handler = new HpChangeHandler(new FixedRollService(1));
        var result = await handler.ApplyAsync(
            new HpChange { CharacterId = caster.Id, Delta = -10 },
            context, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(caster.SystemStats.StatusEffects, e => e.Name.Contains("Concentration"));
        Assert.Null(bound.ControlledById);
        Assert.True(bound.MinionBinding!.ControlLapsed);
        Assert.DoesNotContain(bound.Id, caster.ControlsMinionIds);
        Assert.Equal(caster.Id, plain.ControlledById);
        Assert.False(plain.MinionBinding!.ControlLapsed);
        Assert.Contains(plain.Id, caster.ControlsMinionIds);
    }

    [Fact]
    public async Task HpChange_DownedCaster_LapsesAllMinions()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var caster = ConcentratingCaster();
        var bound = BoundMinion("chars/bound", "Bound", concentrationBound: true, SummonDisposition.Hostile);
        var plain = BoundMinion("chars/plain", "Plain", concentrationBound: false, SummonDisposition.Loyal);
        caster.ControlsMinionIds.Add(bound.Id);
        caster.ControlsMinionIds.Add(plain.Id);
        var context = CreateHpContext(session, new Dictionary<string, Character>
        {
            [caster.Id] = caster,
            [bound.Id] = bound,
            [plain.Id] = plain,
        });

        var handler = new HpChangeHandler(new FixedRollService(20));
        var result = await handler.ApplyAsync(
            new HpChange { CharacterId = caster.Id, Delta = -60 },
            context, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, caster.CurrentHp);
        Assert.Null(bound.ControlledById);
        Assert.Null(plain.ControlledById);
        Assert.Empty(caster.ControlsMinionIds);
    }

    [Fact]
    public async Task StatusExpiry_DayElapsed_LapsesExpiredBindings()
    {
        var caster = new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            SystemStats = new Dnd5eExtension(),
        };
        var minion = new Character
        {
            Id = "chars/minion",
            Name = "Skeleton",
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding
            {
                ControllerId = caster.Id,
                SpellName = "animate_dead",
                DurationDays = 1,
                ExpiresAtDay = 10,
            },
            SystemStats = new Dnd5eExtension(),
        };
        caster.ControlsMinionIds.Add(minion.Id);

        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(minion.Id, Arg.Any<CancellationToken>()).Returns(minion);
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 15 },
            [],
            [caster],
            session,
            1,
            "lifecycle-test");

        var rule = new StatusExpiryRule(RulesetDataTestHelper.CreateConditionProvider());
        var result = await rule.ApplyAsync(context, TestContext.Current.CancellationToken);

        var clear = Assert.Single(result.Deltas.OfType<CharacterUpdate>(), u => u.ClearMinionLink == true);
        Assert.Equal(minion.Id, clear.CharacterId);
        var prune = Assert.Single(result.Deltas.OfType<CharacterUpdate>(), u => u.CharacterId == caster.Id);
        Assert.Equal([minion.Id], prune.ControlsMinionIdsRemove);
        Assert.Contains(result.Narratives, n => n.Text.Contains("duration elapsed"));
    }

    [Fact]
    public async Task StatusExpiry_BindingNotYetExpired_LeavesMinionAlone()
    {
        var caster = new Character
        {
            Id = "chars/caster",
            Name = "Caster",
            SystemStats = new Dnd5eExtension(),
        };
        var minion = new Character
        {
            Id = "chars/minion",
            Name = "Skeleton",
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding
            {
                ControllerId = caster.Id,
                SpellName = "animate_dead",
                DurationDays = 1,
                ExpiresAtDay = 20,
            },
            SystemStats = new Dnd5eExtension(),
        };
        caster.ControlsMinionIds.Add(minion.Id);

        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(minion.Id, Arg.Any<CancellationToken>()).Returns(minion);
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 15 },
            [],
            [caster],
            session,
            1,
            "lifecycle-test");

        var rule = new StatusExpiryRule(RulesetDataTestHelper.CreateConditionProvider());
        var result = await rule.ApplyAsync(context, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Deltas.OfType<CharacterUpdate>(), u => u.ClearMinionLink == true);
    }

    [Theory]
    [InlineData(SummonDisposition.Hostile, "breaks free and turns hostile")]
    [InlineData(SummonDisposition.Neutral, "acts on its own (neutral)")]
    [InlineData(SummonDisposition.Loyal, "still loyal")]
    public void LapseNarrative_MatchesDisposition(SummonDisposition disposition, string fragment)
    {
        var minion = new Character
        {
            Id = "chars/minion",
            Name = "Skelly",
            MinionBinding = new MinionBinding { ControllerId = "chars/caster", Disposition = disposition },
        };

        Assert.Contains(fragment, MinionLapse.LapseNarrative(minion, "loses its binder."));
    }
}
