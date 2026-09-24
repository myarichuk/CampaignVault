using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Modes;
using CampaignVault.Tools;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Combat lifecycle events (published out of band from CombatTools) and the Exclusive-claim turn skip.</summary>
[Collection("RavenDB")]
public class CombatEventTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CombatEventTests(RavenDBFixture fixture) => _fixture = fixture;

    private sealed class Recorder(Func<DomainEvent, IReadOnlyList<WorldChange>>? react = null) : IDomainEventHandler
    {
        public List<DomainEvent> Received { get; } = [];
        public IReadOnlyCollection<string> Topics => [CoreEvents.CombatStarted, CoreEvents.CombatTurnStarted, CoreEvents.CombatEnded];

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
        {
            Received.Add(e);
            return Task.FromResult(react?.Invoke(e) ?? []);
        }
    }

    private CombatTools CreateCombat(IDomainEventHandler subscriber, IInteractionModeSelector? modes = null)
    {
        var repo = _fixture.CreateRepository(overrides: b => b.RegisterInstance(subscriber).As<IDomainEventHandler>());
        return new CombatTools(repo, _fixture.Container.Resolve<CampaignDocumentKeys>(),
            _fixture.Container.Resolve<IRulesetModuleSelector>(), modeSelector: modes);
    }

    private async Task<(string Campaign, string[] Ids)> SeedAsync(int count)
    {
        var campaign = "combat-events-" + Guid.NewGuid().ToString("N")[..8];
        var ids = Enumerable.Range(0, count).Select(i => $"chars/fighter{i}-{Guid.NewGuid():N}").ToArray();
        using var session = _fixture.Store.OpenAsyncSession();
        foreach (var id in ids)
        {
            await session.StoreAsync(new Character { Id = id, Name = id, CurrentHp = 10, MaxHp = 10 });
        }

        await session.SaveChangesAsync();
        return (campaign, ids);
    }

    [Fact]
    public async Task Lifecycle_PublishesStarted_TurnStarted_AndEnded()
    {
        var (campaign, ids) = await SeedAsync(2);
        var recorder = new Recorder();
        var combat = CreateCombat(recorder);

        var started = await combat.StartCombat("locations/arena", [.. ids], campaign);
        Assert.True(started.Success, started.Summary);
        var next = await combat.NextTurn(campaign);
        Assert.True(next.Success, next.Summary);
        var ended = await combat.EndCombat(campaign);
        Assert.True(ended.Success, ended.Summary);

        Assert.Equal(
            [CoreEvents.CombatStarted, CoreEvents.CombatTurnStarted, CoreEvents.CombatTurnStarted, CoreEvents.CombatEnded],
            recorder.Received.Select(e => e.Topic));
        Assert.True(recorder.Received[1].TryGet<string>(CoreEvents.Fields.CharacterId, out var firstActor));
        Assert.Equal(started.Data!.ActiveTurnId, firstActor);
        Assert.True(recorder.Received[2].TryGet<string>(CoreEvents.Fields.CharacterId, out var secondActor));
        Assert.Equal(next.Data!.ActiveTurnId, secondActor);
        Assert.True(recorder.Received[3].TryGet<string>(CoreEvents.Fields.Reason, out var reason));
        Assert.Equal("ended", reason);
    }

    [Fact]
    public async Task ExclusivelyHeldBody_NeverGetsATurn_ButReactionsCanStillHitIt()
    {
        var (campaign, ids) = await SeedAsync(3);
        var body = ids[0];
        var keys = _fixture.Container.Resolve<CampaignDocumentKeys>();
        Assert.True(CampaignSlug.TryCanonicalize(campaign, out var effective));

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig { Id = keys.Config(effective), EnabledModeIds = ["astral"] });
            await session.StoreAsync(new ModeEncounter
            {
                Id = keys.ModeCurrent(effective, "astral"),
                ModeId = "astral",
                IsActive = true,
                Participants = [new ModeParticipantState { CharacterId = body }]
            });
            await session.SaveChangesAsync();
        }

        var astral = Substitute.For<IInteractionMode>();
        astral.ParticipantClaim.Returns(ModeParticipantClaim.Exclusive);
        var modes = Substitute.For<IInteractionModeSelector>();
        modes.TryGetMode("astral").Returns(astral);

        // Stands in for a real-combat plugin: every turn, something swings at the abandoned body.
        var recorder = new Recorder(e => e.Topic == CoreEvents.CombatTurnStarted
            ? [new HpChange { CharacterId = body, Delta = -1 }]
            : []);
        var combat = CreateCombat(recorder, modes);

        var started = await combat.StartCombat("locations/arena", [.. ids], campaign);
        Assert.True(started.Success, started.Summary);
        var actors = new List<string?> { started.Data!.ActiveTurnId };
        for (var i = 0; i < 4; i++)
        {
            var next = await combat.NextTurn(campaign);
            Assert.True(next.Success, next.Summary);
            Assert.Contains("Skipped (held by an exclusive mode)", next.Summary);
            actors.Add(next.Data!.ActiveTurnId);
        }

        Assert.DoesNotContain(body, actors);
        Assert.Equal(2, actors.Distinct().Count());

        using var verify = _fixture.Store.OpenAsyncSession();
        var savedBody = await verify.LoadAsync<Character>(body);
        Assert.Equal(10 - 5, savedBody.CurrentHp);
    }
}
