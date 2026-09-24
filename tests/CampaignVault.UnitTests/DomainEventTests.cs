using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Events;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets.Modes;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class DomainEventTests
{
    // Types in this assembly publish as a plugin would: source = lowercased assembly name.
    private const string TestSource = "campaignvault.unittests";
    private const string Ping = TestSource + ".ping.v1";

    private sealed class PingChange : WorldChange;

    private sealed class FollowUpChange : WorldChange;

    private sealed class InlineHandler(Func<WorldChange, bool> claims, Func<WorldChange, IChangeContext, Task<ChangeHandlerResult>> apply)
        : IWorldChangeHandler
    {
        public bool ShouldHandle(WorldChange change) => claims(change);

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default) =>
            apply(change, context);
    }

    private sealed class Subscriber(string[] topics, Func<DomainEvent, IChangeContext, IReadOnlyList<WorldChange>>? react = null)
        : IDomainEventHandler
    {
        public List<DomainEvent> Received { get; } = [];
        public IReadOnlyCollection<string> Topics => topics;

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
        {
            Received.Add(e);
            return Task.FromResult(react?.Invoke(e, ctx) ?? []);
        }
    }

    private sealed class ThrowingSubscriber : IDomainEventHandler
    {
        public IReadOnlyCollection<string> Topics => [Ping];

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private static InlineHandler Publishing(string topic, object? data = null, bool succeed = true) =>
        new(c => c is PingChange, (_, ctx) =>
        {
            ctx.Publish(topic, data);
            return Task.FromResult(succeed ? ChangeHandlerResult.Ok : ChangeHandlerResult.Failure("nope"));
        });

    private static Task<CommitResult> Dispatch(
        IEnumerable<IWorldChangeHandler> handlers, IEnumerable<IDomainEventHandler> subscribers, params WorldChange[] changes) =>
        new WorldChangeDispatcher(handlers, new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance,
                eventHandlers: subscribers)
            .DispatchAsync(null!, changes, "test",
                () => Task.FromResult(new CampaignTime()),
                () => Task.FromResult(new Dictionary<string, string>()),
                _ => Task.CompletedTask);

    [Fact]
    public async Task OwnPrefixEvent_IsDelivered_WithHostStampedSourceAndDepth()
    {
        var subscriber = new Subscriber([Ping]);

        var result = await Dispatch([Publishing(Ping, new { amount = 5 })], [subscriber], new PingChange());

        Assert.True(result.Success);
        var received = Assert.Single(subscriber.Received);
        Assert.Equal(TestSource, received.Source);
        Assert.Equal(0, received.Depth);
        Assert.True(received.TryGet<int>("amount", out var amount));
        Assert.Equal(5, amount);
    }

    [Theory]
    [InlineData("core.character_damaged.v1")]
    [InlineData("astral.projection_ended.v1")]
    [InlineData(TestSource)]
    public async Task ForeignOrBarePrefix_IsRejected(string topic)
    {
        var subscriber = new Subscriber([topic]);

        var result = await Dispatch([Publishing(topic)], [subscriber], new PingChange());

        Assert.True(result.Success);
        Assert.Empty(subscriber.Received);
    }

    [Fact]
    public async Task FollowUpChange_FromSubscriber_IsDispatchedInSameCommit()
    {
        var followUpsApplied = 0;
        var followUpHandler = new InlineHandler(c => c is FollowUpChange, (_, _) =>
        {
            followUpsApplied++;
            return Task.FromResult(ChangeHandlerResult.Ok);
        });
        var subscriber = new Subscriber([Ping], (_, _) => [new FollowUpChange()]);

        var result = await Dispatch([Publishing(Ping), followUpHandler], [subscriber], new PingChange());

        Assert.True(result.Success);
        Assert.Equal(1, followUpsApplied);
    }

    [Fact]
    public async Task FailingFollowUp_FailsTheCommit()
    {
        var failing = new InlineHandler(c => c is FollowUpChange, (_, _) => Task.FromResult(ChangeHandlerResult.Failure("no")));
        var subscriber = new Subscriber([Ping], (_, _) => [new FollowUpChange()]);

        var result = await Dispatch([Publishing(Ping), failing], [subscriber], new PingChange());

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RepublishingLoop_StopsAtDepthCap()
    {
        var subscriber = new Subscriber([Ping], (_, ctx) =>
        {
            ctx.Publish(Ping);
            return [];
        });

        var result = await Dispatch([Publishing(Ping)], [subscriber], new PingChange());

        Assert.True(result.Success);
        Assert.Equal(WorldChangeDispatcher.MaxEventDepth, subscriber.Received.Count);
        Assert.Equal(Enumerable.Range(0, WorldChangeDispatcher.MaxEventDepth), subscriber.Received.Select(e => e.Depth));
    }

    [Fact]
    public async Task ThrowingSubscriber_IsSwallowed_AndOthersStillReceive()
    {
        var subscriber = new Subscriber([Ping]);

        var result = await Dispatch([Publishing(Ping)], [new ThrowingSubscriber(), subscriber], new PingChange());

        Assert.True(result.Success);
        Assert.Single(subscriber.Received);
    }

    [Fact]
    public async Task FailedChange_DiscardsItsEvents()
    {
        var subscriber = new Subscriber([Ping]);

        await Dispatch([Publishing(Ping, succeed: false)], [subscriber], new PingChange());

        Assert.Empty(subscriber.Received);
    }

    [Fact]
    public async Task DamageFromChildMutation_PublishesCoreEvent_AttributedToRulesetActor()
    {
        // Mirrors RulesetActionHandler: the top-level ruleset_action dispatches its HpChange as a child mutation.
        var fakeRuleset = new InlineHandler(c => c is RulesetAction, async (_, ctx) =>
        {
            var context = (ChangeContext)ctx;
            context.RegisterNewCharacter(new Character { Id = "chars/aang", MaxHp = 10, CurrentHp = 3 });
            await context.Dispatcher.DispatchMutationAsync(ctx, new HpChange { CharacterId = "chars/aang", Delta = -7 });
            return ChangeHandlerResult.Ok;
        });
        var subscriber = new Subscriber([CoreEvents.CharacterDamaged]);

        var result = await Dispatch(
            [fakeRuleset, new HpChangeHandler(Substitute.For<IRollService>())], [subscriber],
            new RulesetAction { ActionType = RulesetActionType.Attack, CharacterId = "chars/firelord", TargetIds = ["chars/aang"] });

        Assert.True(result.Success);
        var damaged = Assert.Single(subscriber.Received);
        Assert.Equal(CoreEvents.Source, damaged.Source);
        Assert.True(damaged.TryGet<string>(CoreEvents.Fields.CharacterId, out var target));
        Assert.Equal("chars/aang", target);
        Assert.True(damaged.TryGet<int>(CoreEvents.Fields.Amount, out var amount));
        Assert.Equal(7, amount);
        Assert.True(damaged.TryGet<int>(CoreEvents.Fields.HpLost, out var lost));
        Assert.Equal(3, lost);
        Assert.True(damaged.TryGet<string>(CoreEvents.Fields.ActorId, out var actor));
        Assert.Equal("chars/firelord", actor);
    }

    [Fact]
    public async Task DamageToBodyAlreadyAtZero_StillPublishes()
    {
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { ["chars/aang"] = new() { Id = "chars/aang", MaxHp = 10, CurrentHp = 0 } });

        await new HpChangeHandler(Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = "chars/aang", Delta = -4 }, context);

        var damaged = Assert.Single(context.TakePendingEvents());
        Assert.True(damaged.TryGet<int>(CoreEvents.Fields.HpLost, out var lost));
        Assert.Equal(0, lost);
        Assert.False(damaged.TryGet<string>(CoreEvents.Fields.ActorId, out _));
    }

    [Fact]
    public async Task ModeEnterAndExit_PublishCoreEvents()
    {
        var mode = Substitute.For<IInteractionMode>();
        mode.ModeId.Returns("crafting");
        mode.DisplayName.Returns("Crafting");
        mode.CompatibleSystems.Returns([]);
        var stateMachine = Substitute.For<IModeStateMachine>();
        stateMachine.CreateEncounter(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(ci => new ModeEncounter
            {
                LocationId = ci.ArgAt<string>(0),
                Participants = ci.ArgAt<IReadOnlyList<string>>(1).Select(id => new ModeParticipantState { CharacterId = id }).ToList()
            });
        mode.StateMachine.Returns(stateMachine);
        var selector = new InteractionModeSelector([mode]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<ModeEncounter>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ModeEncounter)null!);
        var context = ChangeContextTestHelper.Create(session: session, campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] });

        await handler.ApplyAsync(new ModeTransitionChange
        {
            ModeId = "crafting", Action = "enter", LocationId = "locations/forge", ParticipantIds = ["chars/smith"]
        }, context);

        var entered = Assert.Single(context.TakePendingEvents());
        Assert.Equal(CoreEvents.ModeEntered, entered.Topic);
        Assert.True(entered.TryGet<List<string>>(CoreEvents.Fields.ParticipantIds, out var participants));
        Assert.Equal(["chars/smith"], participants);
    }

    [Fact]
    public void TryGet_IsTolerant()
    {
        var e = DomainEvent.Create(Ping, new { amount = 5, name = "x", ids = new[] { "a", "b" }, gone = (string?)null });

        Assert.True(e.TryGet<string>("name", out var name));
        Assert.Equal("x", name);
        Assert.True(e.TryGet<List<string>>("ids", out var ids));
        Assert.Equal(["a", "b"], ids);
        Assert.False(e.TryGet<int>("name", out _));
        Assert.False(e.TryGet<int>("missing", out _));
        Assert.False(e.TryGet<string>("gone", out _));
    }

    [Fact]
    public void Create_WrapsNonObjectPayload()
    {
        var e = DomainEvent.Create(Ping, 42);

        Assert.True(e.TryGet<int>("value", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void Sources_UseManifestId_NeverCore_AndOnlyKeepOwnDeclaredTopics()
    {
        var sdk = typeof(IChangeContext).Assembly;
        var test = typeof(DomainEventTests).Assembly;
        var plugin = typeof(FactAttribute).Assembly; // any non-host assembly stands in for a loaded plugin

        var sources = new PluginEventSources([
            (plugin, "Crafting", ["crafting.item_finished.v1", "core.spoof.v1"]),
            (test, "core", [])
        ]);

        Assert.Equal(CoreEvents.Source, sources.SourceFor(typeof(HpChangeHandler)));
        Assert.Equal(CoreEvents.Source, sources.SourceFor(sdk.GetTypes()[0]));
        Assert.Equal("crafting", sources.SourceFor(typeof(FactAttribute)));
        Assert.Equal(TestSource, sources.SourceFor(typeof(DomainEventTests)));
        Assert.Contains("crafting.item_finished.v1", sources.DeclaredTopics);
        Assert.DoesNotContain("core.spoof.v1", sources.DeclaredTopics);
        Assert.Contains(CoreEvents.CharacterDamaged, sources.DeclaredTopics);
    }
}
