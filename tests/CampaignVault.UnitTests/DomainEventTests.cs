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

    private sealed class Subscriber(
        string[] topics,
        Func<DomainEvent, IChangeContext, IReadOnlyList<WorldChange>>? react = null,
        ReactionFailurePolicy policy = ReactionFailurePolicy.Isolate)
        : IDomainEventHandler
    {
        public List<DomainEvent> Received { get; } = [];
        public IReadOnlyCollection<string> Topics => topics;
        public ReactionFailurePolicy FailurePolicy => policy;

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
        {
            Received.Add(e);
            return Task.FromResult(react?.Invoke(e, ctx) ?? []);
        }
    }

    private sealed class ThrowingSubscriber(string topic = Ping, Exception? toThrow = null, string? publishFirst = null)
        : IDomainEventHandler
    {
        public int Calls { get; private set; }
        public IReadOnlyCollection<string> Topics => [topic];

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
        {
            Calls++;
            if (publishFirst != null)
            {
                ctx.Publish(publishFirst);
            }

            throw toThrow ?? new InvalidOperationException("boom");
        }
    }

    private sealed class RecordingObserver : IWorldChangeObserver
    {
        public List<WorldChange> Seen { get; } = [];
        public bool IsInterestedIn(WorldChange change, IChangeContext context) => true;

        public Task OnCommittedAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
        {
            Seen.Add(change);
            return Task.CompletedTask;
        }
    }

    private sealed class OkChange : WorldChange;

    private static InlineHandler Ok() => new(c => c is OkChange, (_, _) => Task.FromResult(ChangeHandlerResult.Ok));

    private static InlineHandler FailingFollowUp(string? publishFirst = null) =>
        new(c => c is FollowUpChange, (_, ctx) =>
        {
            if (publishFirst != null)
            {
                ctx.Publish(publishFirst);
            }

            return Task.FromResult(ChangeHandlerResult.Failure("no such body"));
        });

    private static InlineHandler Publishing(string topic, object? data = null, bool succeed = true) =>
        new(c => c is PingChange, (_, ctx) =>
        {
            ctx.Publish(topic, data);
            return Task.FromResult(succeed ? ChangeHandlerResult.Ok : ChangeHandlerResult.Failure("nope"));
        });

    private static Task<CommitResult> Dispatch(
        IEnumerable<IWorldChangeHandler> handlers, IEnumerable<IDomainEventHandler> subscribers, params WorldChange[] changes) =>
        Dispatch(handlers, subscribers, [], changes);

    private static Task<CommitResult> Dispatch(
        IEnumerable<IWorldChangeHandler> handlers, IEnumerable<IDomainEventHandler> subscribers,
        IEnumerable<IWorldChangeObserver> observers, params WorldChange[] changes) =>
        new WorldChangeDispatcher(handlers, new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance,
                observers: observers, eventHandlers: subscribers)
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
    public async Task FailingFollowUp_IsIsolated_CommitKept_FaultReported()
    {
        var subscriber = new Subscriber([Ping], (_, _) => [new FollowUpChange()]);

        var result = await Dispatch([Publishing(Ping), FailingFollowUp()], [subscriber], new PingChange());

        Assert.True(result.Success);
        var fault = Assert.Single(result.PluginFaults);
        Assert.Equal(PluginFault.FollowUpFailed, fault.Stage);
        Assert.Equal(TestSource, fault.PluginId);
        Assert.Equal(nameof(FollowUpChange), fault.ChangeType);
        Assert.Contains("no such body", fault.Message);
        Assert.True(fault.CommitKept);
        Assert.Empty(fault.AppliedChangeTypes);
        Assert.Contains(result.Summary, line => line.StartsWith("PLUGIN FAULT"));
    }

    [Fact]
    public async Task PartialReaction_StopsAtFault_AndListsWhatLanded()
    {
        var observer = new RecordingObserver();
        var subscriber = new Subscriber([Ping], (_, _) => [new OkChange(), new FollowUpChange(), new OkChange()]);

        var result = await Dispatch([Publishing(Ping), Ok(), FailingFollowUp()], [subscriber], [observer], new PingChange());

        Assert.True(result.Success);
        var fault = Assert.Single(result.PluginFaults);
        Assert.Equal([nameof(OkChange)], fault.AppliedChangeTypes);
        Assert.Single(result.ReactionChanges.OfType<OkChange>());
        Assert.Single(observer.Seen.OfType<OkChange>());
    }

    [Fact]
    public async Task FailCommitPolicy_FailsTheCommit_AndStillReportsTheFault()
    {
        var subscriber = new Subscriber([Ping], (_, _) => [new FollowUpChange()], ReactionFailurePolicy.FailCommit);

        var result = await Dispatch([Publishing(Ping), FailingFollowUp()], [subscriber], new PingChange());

        Assert.False(result.Success);
        var fault = Assert.Single(result.PluginFaults);
        Assert.False(fault.CommitKept);
        Assert.Empty(result.ReactionChanges);
    }

    [Fact]
    public async Task FailedFollowUp_DropsTheEventsItPublished()
    {
        const string Echo = TestSource + ".echo.v1";
        var listener = new Subscriber([Echo]);
        var subscriber = new Subscriber([Ping], (_, _) => [new FollowUpChange()]);

        await Dispatch([Publishing(Ping), FailingFollowUp(publishFirst: Echo)], [subscriber, listener], new PingChange());

        Assert.Empty(listener.Received);
    }

    [Fact]
    public async Task SuccessfulReaction_IsVisibleToObserversAndResult()
    {
        var observer = new RecordingObserver();
        var subscriber = new Subscriber([Ping], (_, _) => [new OkChange()]);

        var result = await Dispatch([Publishing(Ping), Ok()], [subscriber], [observer], new PingChange());

        Assert.True(result.Success);
        Assert.IsType<OkChange>(Assert.Single(result.ReactionChanges));
        Assert.Contains(observer.Seen, c => c is OkChange);
        Assert.Empty(result.PluginFaults);
    }

    [Fact]
    public async Task RepublishingLoop_StopsAtDepthCap()
    {
        var subscriber = new Subscriber([Ping], (_, ctx) =>
        {
            ctx.Publish(Ping);
            return [];
        });

        var faultListener = new Subscriber([CoreEvents.PluginFaulted]);

        var result = await Dispatch([Publishing(Ping)], [subscriber, faultListener], new PingChange());

        Assert.True(result.Success);
        Assert.Equal(WorldChangeDispatcher.MaxEventDepth, subscriber.Received.Count);
        Assert.Equal(Enumerable.Range(0, WorldChangeDispatcher.MaxEventDepth), subscriber.Received.Select(e => e.Depth));
        Assert.Equal(PluginFault.DepthCapped, Assert.Single(result.PluginFaults).Stage);
        // The fault event goes out in the fault phase at depth 0, so the cap that caused it doesn't swallow it.
        var faulted = Assert.Single(faultListener.Received);
        Assert.Equal(0, faulted.Depth);
        Assert.True(faulted.TryGet<string>(CoreEvents.Fields.Stage, out var stage));
        Assert.Equal(PluginFault.DepthCapped, stage);
    }

    [Fact]
    public async Task ThrowingSubscriber_IsIsolated_AndOthersStillReceive()
    {
        var subscriber = new Subscriber([Ping]);

        var result = await Dispatch([Publishing(Ping)], [new ThrowingSubscriber(), subscriber], new PingChange());

        Assert.True(result.Success);
        Assert.Single(subscriber.Received);
        var fault = Assert.Single(result.PluginFaults);
        Assert.Equal(PluginFault.HandlerThrew, fault.Stage);
        Assert.Contains("boom", fault.Message);
    }

    [Fact]
    public async Task ThrowingSubscriber_EventsPublishedBeforeTheThrow_AreDropped()
    {
        const string Echo = TestSource + ".echo.v1";
        var listener = new Subscriber([Echo]);

        await Dispatch([Publishing(Ping)], [new ThrowingSubscriber(publishFirst: Echo), listener], new PingChange());

        Assert.Empty(listener.Received);
    }

    [Fact]
    public async Task PluginFaultException_CarriesItsFixHint_IntoTheFaultEvent()
    {
        var faultListener = new Subscriber([CoreEvents.PluginFaulted]);
        var thrower = new ThrowingSubscriber(toThrow: new PluginFaultException("no astral link", "Enter astral mode first."));

        var result = await Dispatch([Publishing(Ping)], [thrower, faultListener], new PingChange());

        Assert.Equal("Enter astral mode first.", Assert.Single(result.PluginFaults).FixHint);
        var faulted = Assert.Single(faultListener.Received);
        Assert.Equal(CoreEvents.Source, faulted.Source);
        Assert.True(faulted.TryGet<string>(CoreEvents.Fields.FixHint, out var hint));
        Assert.Equal("Enter astral mode first.", hint);
        Assert.True(faulted.TryGet<string>(CoreEvents.Fields.PluginId, out var pluginId));
        Assert.Equal(TestSource, pluginId);
        Assert.True(faulted.TryGet<bool>(CoreEvents.Fields.CommitKept, out var kept) && kept);
    }

    [Fact]
    public async Task BrokenFaultListener_DoesNotFeedOnItsOwnFaults()
    {
        var brokenListener = new ThrowingSubscriber(topic: CoreEvents.PluginFaulted);

        var result = await Dispatch([Publishing(Ping)], [new ThrowingSubscriber(), brokenListener], new PingChange());

        Assert.True(result.Success);
        Assert.Equal(1, brokenListener.Calls);
        Assert.Equal(2, result.PluginFaults.Count);
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
            new RulesetAction { ActionType = RulesetActionType.Attack, ActionName = "Attack", CharacterId = "chars/firelord", TargetIds = ["chars/aang"] });

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
    public async Task Downed_IsPublishedOnlyWhenHpCrossesToZero()
    {
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character>
            {
                ["chars/aang"] = new() { Id = "chars/aang", MaxHp = 10, CurrentHp = 3 },
                ["chars/zuko"] = new() { Id = "chars/zuko", MaxHp = 10, CurrentHp = 0 }
            });
        var handler = new HpChangeHandler(Substitute.For<IRollService>());

        await handler.ApplyAsync(new HpChange { CharacterId = "chars/aang", Delta = -5 }, context);
        await handler.ApplyAsync(new HpChange { CharacterId = "chars/zuko", Delta = -5 }, context);

        var downed = Assert.Single(context.TakePendingEvents(), e => e.Topic == CoreEvents.CharacterDowned);
        Assert.True(downed.TryGet<string>(CoreEvents.Fields.CharacterId, out var id));
        Assert.Equal("chars/aang", id);
    }

    [Fact]
    public async Task EventOccurred_PublishesEventLogged_WithCategory()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var context = ChangeContextTestHelper.Create(session: session, campaignName: "test");

        await new EventOccurredHandler().ApplyAsync(new EventOccurred
        {
            Summary = "Aang and Katara talk by the pond.",
            Category = EventCategory.Conversation,
            Involved = ["chars/aang", "chars/katara"],
            InitiatorId = "chars/katara",
            EventId = "events/pond-talk",
            Importance = MemoryImportance.Important
        }, context);

        var logged = Assert.Single(context.TakePendingEvents(), e => e.Topic == CoreEvents.EventLogged);
        Assert.True(logged.TryGet<string>(CoreEvents.Fields.Category, out var category));
        Assert.Equal("Conversation", category);
        Assert.True(logged.TryGet<List<string>>(CoreEvents.Fields.Involved, out var involved));
        Assert.Equal(["chars/aang", "chars/katara"], involved);
        Assert.True(logged.TryGet<string>(CoreEvents.Fields.InitiatorId, out var initiator));
        Assert.Equal("chars/katara", initiator);
    }

    [Fact]
    public async Task CompletedTravel_PublishesTraveled_WithOrigin()
    {
        var traveler = new Character { Id = "chars/aang", Name = "Aang", CurrentLocationId = "locations/temple" };
        var context = ChangeContextTestHelper.Create(campaignName: "test",
            characters: new Dictionary<string, Character> { [traveler.Id] = traveler },
            locations: new Dictionary<string, Location>
            {
                ["locations/temple"] = new() { Id = "locations/temple", Name = "Temple" },
                ["locations/pond"] = new() { Id = "locations/pond", Name = "Pond" }
            });

        var result = await new TravelChangeHandler(new EncounterResolver(() => 0.99)).ApplyAsync(
            new TravelChange { CharacterId = traveler.Id, DestinationLocationId = "locations/pond", TravelCostHoursOverride = 2 },
            context);

        Assert.True(result.Success, result.Message);
        var traveled = Assert.Single(context.TakePendingEvents(), e => e.Topic == CoreEvents.Traveled);
        Assert.True(traveled.TryGet<string>(CoreEvents.Fields.FromLocationId, out var from));
        Assert.Equal("locations/temple", from);
        Assert.True(traveled.TryGet<string>(CoreEvents.Fields.LocationId, out var to));
        Assert.Equal("locations/pond", to);
        Assert.True(traveled.TryGet<double>(CoreEvents.Fields.Hours, out var hours));
        Assert.Equal(2, hours);
    }

    [Fact]
    public async Task CompletedRest_PublishesRested_InterruptedRestDoesNot()
    {
        Character Sleeper() => new() { Id = "chars/aang", Name = "Aang", CurrentLocationId = "locations/pond" };
        ChangeContext Context(Character c) => ChangeContextTestHelper.Create(campaignName: "test",
            characters: new Dictionary<string, Character> { [c.Id] = c },
            locations: new Dictionary<string, Location> { ["locations/pond"] = new() { Id = "locations/pond", Name = "Pond" } });
        var rest = new RestChange { CharacterId = "chars/aang", LocationId = "locations/pond", IntendedHours = 8 };
        var conditions = RulesetDataTestHelper.CreateServices().Conditions;

        var calm = Context(Sleeper());
        await new RestChangeHandler(new EncounterResolver(() => 0.99), conditions).ApplyAsync(rest, calm);
        var rested = Assert.Single(calm.TakePendingEvents(), e => e.Topic == CoreEvents.Rested);
        Assert.True(rested.TryGet<string>(CoreEvents.Fields.RestType, out var restType));
        Assert.Equal(nameof(RestType.LongRest), restType);

        var ambushed = Context(Sleeper());
        await new RestChangeHandler(new EncounterResolver(() => 0.0), conditions).ApplyAsync(rest, ambushed);
        var events = ambushed.TakePendingEvents();
        Assert.DoesNotContain(events, e => e.Topic == CoreEvents.Rested);
        Assert.Contains(events, e => e.Topic == CoreEvents.EncounterInterrupted);
    }

    [Fact]
    public async Task EncounterRoll_PublishesEncounterInterrupted_OnlyWhenItFires()
    {
        var context = ChangeContextTestHelper.Create(campaignName: "test");
        var character = new Character { Id = "chars/aang", Name = "Aang" };
        var location = new Location { Id = "locations/swamp", Name = "Swamp", DangerModifier = 10 };

        await new EncounterResolver(() => 0.99).EvaluateAsync(context, character, location, 8, 4, 0, "Travel");
        Assert.Empty(context.TakePendingEvents());

        var (interrupted, _, deltas, _) = await new EncounterResolver(() => 0.0)
            .EvaluateAsync(context, character, location, 8, 4, 0, "Travel");

        Assert.True(interrupted);
        var ambush = Assert.Single(context.TakePendingEvents());
        Assert.Equal(CoreEvents.EncounterInterrupted, ambush.Topic);
        Assert.True(ambush.TryGet<string>(CoreEvents.Fields.Context, out var ctxType));
        Assert.Equal("Travel", ctxType);
        Assert.True(ambush.TryGet<string>(CoreEvents.Fields.SpawnedId, out var spawned));
        Assert.Contains(deltas.OfType<CharacterCreate>(), c => c.CharacterId == spawned);
    }

    [Fact]
    public async Task OutOfBandPublish_DeliversAndAppliesReactions_OnTheCallersCharacters()
    {
        var body = new Character { Id = "chars/aang", MaxHp = 10, CurrentHp = 10 };
        var subscriber = new Subscriber([CoreEvents.CombatTurnStarted],
            (e, _) => e.TryGet<string>(CoreEvents.Fields.CharacterId, out var id) ? [new HpChange { CharacterId = id, Delta = -2 }] : []);
        var dispatcher = new WorldChangeDispatcher([new HpChangeHandler(Substitute.For<IRollService>())], new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance, eventHandlers: [subscriber]);
        var session = Substitute.For<IAsyncDocumentSession>();

        var result = await dispatcher.PublishAsync(session, "test",
            [(CoreEvents.CombatTurnStarted, new Dictionary<string, object?> { [CoreEvents.Fields.CharacterId] = "chars/aang" })],
            [body], null,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Single(subscriber.Received);
        Assert.Equal(8, body.CurrentHp);
        Assert.IsType<HpChange>(Assert.Single(result.ReactionChanges));
    }

    [Fact]
    public async Task OutOfBandPublish_WithNoSubscriber_TouchesNothing()
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var session = Substitute.For<IAsyncDocumentSession>();

        var result = await dispatcher.PublishAsync(session, "test", [(CoreEvents.CombatEnded, null)], [], null,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Empty(session.ReceivedCalls());
    }

    [Fact]
    public void EveryCoreTopic_IsDeclared_AndOwnedByCore()
    {
        var declared = PluginEventSources.CoreOnly.DeclaredTopics;

        Assert.All(CoreEvents.All, topic =>
        {
            Assert.Contains(topic, declared);
            Assert.True(CoreEvents.IsOwnedBy(topic, CoreEvents.Source));
        });
        Assert.Equal(CoreEvents.All.Count, CoreEvents.All.Distinct().Count());
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
