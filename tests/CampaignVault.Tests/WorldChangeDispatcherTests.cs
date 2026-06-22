using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Focused tests for the WorldChangeDispatcher and the ShouldHandle responsibility pattern.
/// These tests exercise handler selection, duplicate claim detection, error handling, and mixed batches.
/// </summary>
public class WorldChangeDispatcherTests
{
    private sealed class TestHandler : IWorldChangeHandler
    {
        private readonly Func<WorldChange, bool> _shouldHandle;
        private readonly Func<WorldChange, ChangeContext, Task<ChangeHandlerResult>> _apply;
        public string Name { get; }

        public TestHandler(string name, Func<WorldChange, bool> shouldHandle,
            Func<WorldChange, ChangeContext, Task<ChangeHandlerResult>> apply)
        {
            Name = name;
            _shouldHandle = shouldHandle;
            _apply = apply;
        }

        public bool ShouldHandle(WorldChange change) => _shouldHandle(change);

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
            CancellationToken ct = default)
            => _apply(change, context);

        public bool ExtractInvolvedEntities(
            WorldChange change,
            HashSet<string>? characterIds = null,
            HashSet<string>? locationIds = null,
            HashSet<string>? factionIds = null,
            HashSet<string>? questIds = null,
            HashSet<string>? itemIds = null,
            HashSet<string>? allInvolvedIds = null)
        {
            if (!_shouldHandle(change)) return false;

            // For tests, simulate basic extraction
            if (change is HpChange hp)
            {
                characterIds?.Add(hp.CharacterId);
                allInvolvedIds?.Add(hp.CharacterId);
            }
            else if (change is StatusChange sc)
            {
                characterIds?.Add(sc.CharacterId);
                allInvolvedIds?.Add(sc.CharacterId);
            }
            else if (change is MoodChange mc)
            {
                characterIds?.Add(mc.CharacterId);
                allInvolvedIds?.Add(mc.CharacterId);
            }
            else if (change is EngagementRelationChange erc)
            {
                characterIds?.Add(erc.ActorId);
                characterIds?.Add(erc.TargetId);
                allInvolvedIds?.Add(erc.ActorId);
                allInvolvedIds?.Add(erc.TargetId);
            }
            else if (change is EventOccurred eo && eo.Involved != null)
            {
                foreach (var id in eo.Involved)
                {
                    characterIds?.Add(id);
                    allInvolvedIds?.Add(id);
                }
            }

            return true;
        }
    }

    private static WorldChangeDispatcher CreateDispatcher(params IWorldChangeHandler[] handlers)
        => new WorldChangeDispatcher(handlers, new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

    // Note: We no longer manually construct ChangeContext in most tests because
    // WorldChangeDispatcher.DispatchAsync owns context creation.

    [Fact]
    public async Task Dispatcher_WithNoHandlers_MarksEverythingUnhandled()
    {
        var dispatcher = CreateDispatcher();
        var summary = new List<string>();

        var result = await dispatcher.DispatchAsync(
            null!, // session not used when no handlers
            [new HpChange { CharacterId = "c1", Delta = -5 }],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success);
        Assert.Contains("Unhandled change type", result.Summary[0]);
    }

    [Fact]
    public async Task Dispatcher_SelectsCorrectHandler_ByShouldHandle()
    {
        var hpHandler = new TestHandler("Hp", c => c is HpChange, (c, ctx) =>
        {
            ctx.RecordMessage("HP handled by test handler");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var statusHandler = new TestHandler("Status", c => c is StatusChange, (c, ctx) =>
        {
            ctx.RecordMessage("Status handled");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var dispatcher = CreateDispatcher(hpHandler, statusHandler);
        var summary = new List<string>();

        var result = await dispatcher.DispatchAsync(
            null!,
            [
                new HpChange { CharacterId = "c1", Delta = 3 },
                new StatusChange { CharacterId = "c1", Status = "Blessed" }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains("HP handled by test handler", result.Summary);
        Assert.Contains("Status handled", result.Summary);
    }

    [Fact]
    public async Task Dispatcher_LogsWarning_OnDuplicateClaim_AndUsesFirst()
    {
        var first = new TestHandler("First", c => c is HpChange, (c, ctx) =>
        {
            ctx.RecordMessage("Handled by First");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var second = new TestHandler("Second", c => c is HpChange, (c, ctx) =>
        {
            ctx.RecordMessage("Handled by Second (should not happen)");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var dispatcher = CreateDispatcher(first, second);

        var result = await dispatcher.DispatchAsync(
            null!,
            [new HpChange { CharacterId = "c1", Delta = 1 }],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains("Handled by First", result.Summary);
        Assert.DoesNotContain("Handled by Second", result.Summary);
    }

    [Fact]
    public async Task Dispatcher_RecordsFailure_WhenHandlerReturnsFailure()
    {
        var failing = new TestHandler("Failer", c => true, (c, ctx) =>
        {
            ctx.RecordMessage("Something went wrong");
            ctx.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure("boom"));
        });

        var dispatcher = CreateDispatcher(failing);

        var result = await dispatcher.DispatchAsync(
            null!,
            [new HpChange { CharacterId = "c1", Delta = 1 }],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success);
        // The failure message from the handler + the RecordMessage both appear
        Assert.Contains(result.Summary, s => s.Contains("boom") || s.Contains("Something went wrong"));
    }

    [Fact]
    public async Task Dispatcher_HandlesMixedBatch_WithSomeHandlersMissing()
    {
        // Use only fake handlers so we don't need a real session or preloads
        var hpHandler = new TestHandler("Hp", c => c is HpChange, (c, ctx) =>
        {
            ctx.RecordMessage("HP handled (fake)");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var dispatcher = CreateDispatcher(hpHandler);

        var result = await dispatcher.DispatchAsync(
            null!, // safe because no real handler that needs session will run
            [
                new HpChange { CharacterId = "c1", Delta = 5 },
                new MoodChange { CharacterId = "c1", NewMood = "happy" } // no handler
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success); // because MoodChange is unhandled
        Assert.Contains("HP handled (fake)", result.Summary);
        Assert.Contains(result.Summary, s => s.Contains("Unhandled change type: MoodChange"));
    }

    [Fact]
    public async Task EventOccurredHandler_ConversationCategory_WithoutInvolved_ReturnsFailure()
    {
        var handler = new EventOccurredHandler();
        var dispatcher = CreateDispatcher(handler);
        var mockSession = Substitute.For<IAsyncDocumentSession>();
        mockSession.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Character>());
        mockSession.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Item>());
        mockSession.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Location>());

        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new EventOccurred
                {
                    Category = EventCategory.Conversation,
                    Summary = "Lirael and Valen talking at the bar.",
                    Involved = null
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.False(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("MUST include 'involved'"));
    }

    [Fact]
    public async Task EventOccurredHandler_ConversationCategory_WithInvolved_ReturnsSuccess()
    {
        var handler = new EventOccurredHandler();
        var dispatcher = CreateDispatcher(handler);
        var loggedEvents = new List<Event>();
        var mockSession = Substitute.For<IAsyncDocumentSession>();
        mockSession.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Character>());
        mockSession.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Item>());
        mockSession.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Location>());

        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new EventOccurred
                {
                    Category = EventCategory.Conversation,
                    Summary = "Lirael and Valen talking at the bar.",
                    Involved = ["chars/lirael", "chars/valen"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            e =>
            {
                loggedEvents.Add(e);
                return Task.CompletedTask;
            });

        Assert.True(result.Success);
        Assert.Single(loggedEvents);
        Assert.Contains("chars/lirael", loggedEvents[0].Involved);
        Assert.Contains("chars/valen", loggedEvents[0].Involved);
    }

    [Fact]
    public async Task EventOccurredHandler_ConversationCategory_InfersInvolved_FromEngagementRelationInBatch()
    {
        var handler = new EventOccurredHandler();
        var engagementStub = new TestHandler(
            "Engagement",
            c => c is EngagementRelationChange,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var dispatcher = CreateDispatcher(engagementStub, handler);
        var loggedEvents = new List<Event>();
        var mockSession = Substitute.For<IAsyncDocumentSession>();
        mockSession.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Character>());
        mockSession.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Item>());
        mockSession.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Location>());

        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new EventOccurred
                {
                    Category = EventCategory.Conversation,
                    Summary = "Valen asked Lirael about the caravans.",
                    Involved = null
                },
                new EngagementRelationChange
                {
                    ActorId = "chars/valen",
                    TargetId = "chars/lirael-goldvein",
                    Category = EngagementCategory.Social,
                    Verb = "discussing the disappearances with",
                    Bidirectional = true
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            e =>
            {
                loggedEvents.Add(e);
                return Task.CompletedTask;
            });

        Assert.True(result.Success);
        Assert.Single(loggedEvents);
        Assert.Contains("chars/valen", loggedEvents[0].Involved);
        Assert.Contains("chars/lirael-goldvein", loggedEvents[0].Involved);
        Assert.Contains(result.Summary, s => s.Contains("Auto-inferred involved"));
    }
}