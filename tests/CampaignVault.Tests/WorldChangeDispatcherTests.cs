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
                characterIds?.Add(erc.CharacterId);
                characterIds?.Add(erc.TargetId);
                allInvolvedIds?.Add(erc.CharacterId);
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
                    CharacterId = "chars/valen",
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

    [Fact]
    public async Task EventOccurredHandler_ConversationCategory_InfersThreeParticipants_FromMultipleEngagements()
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
                    Summary = "The party and the barkeep discuss rumors over ale.",
                    Involved = null
                },
                new EngagementRelationChange
                {
                    CharacterId = "chars/pc",
                    TargetId = "chars/barkeep",
                    Category = EngagementCategory.Social,
                    Verb = "ordering drinks from",
                    Bidirectional = true
                },
                new EngagementRelationChange
                {
                    CharacterId = "chars/companion",
                    TargetId = "chars/barkeep",
                    Category = EngagementCategory.Social,
                    Verb = "listening in on",
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
        Assert.Equal(3, loggedEvents[0].Involved.Count);
        Assert.Contains("chars/pc", loggedEvents[0].Involved);
        Assert.Contains("chars/companion", loggedEvents[0].Involved);
        Assert.Contains("chars/barkeep", loggedEvents[0].Involved);
    }

    [Fact]
    public async Task EventOccurredHandler_EchoesGeneratedEventId_InCommitSummary()
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
                    Category = EventCategory.Discovery,
                    Summary = "Party found the hidden stair.",
                    Involved = ["chars/pc1"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, s => s.StartsWith("Event logged: Party found the hidden stair. (id: events/"));
    }

    [Fact]
    public async Task EventOccurredHandler_UsesClientSuppliedEventId_WhenNoCollision()
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
        // LoadAsync<Event> is left unconfigured — NSubstitute returns null by default, simulating no collision.

        var loggedEvents = new List<Event>();
        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new EventOccurred
                {
                    EventId = "events/valen-lirael-caravans",
                    Category = EventCategory.Discovery,
                    Summary = "Party found the hidden stair.",
                    Involved = ["chars/pc1"]
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
        Assert.Equal("events/valen-lirael-caravans", loggedEvents[0].Id);
        Assert.Contains(result.Summary, s => s.Contains("(id: events/valen-lirael-caravans)"));
    }

    [Fact]
    public async Task EventOccurredHandler_FallsBackToGeneratedId_OnEventIdCollision()
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
        // Simulate a collision: an event already exists at the requested ID.
        mockSession.LoadAsync<Event>("events/valen-lirael-caravans", Arg.Any<CancellationToken>())
            .Returns(new Event { Id = "events/valen-lirael-caravans", Summary = "Pre-existing event" });

        var loggedEvents = new List<Event>();
        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new EventOccurred
                {
                    EventId = "events/valen-lirael-caravans",
                    Category = EventCategory.Discovery,
                    Summary = "A different event entirely.",
                    Involved = ["chars/pc1"]
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
        Assert.NotEqual("events/valen-lirael-caravans", loggedEvents[0].Id);
        Assert.StartsWith("events/", loggedEvents[0].Id);
        Assert.Contains(result.Summary, s => s.Contains("WARNING") && s.Contains("already exists"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Conversation_HintsMissingActivityAndKnowledgeUpdate_WhenBatchHasNeither()
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
                    Summary = "Archivist Wren and Magister Dol argue over the ruin's age.",
                    Involved = ["chars/archivist-wren", "chars/magister-dol"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.Contains(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Conversation_SuppressesHints_WhenActivityAndKnowledgeUpdatePresent()
    {
        var handler = new EventOccurredHandler();
        var activityStub = new TestHandler(
            "Activity",
            c => c is ActivityChange,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var knowledgeStub = new TestHandler(
            "Knowledge",
            c => c is KnowledgeUpdate,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var dispatcher = CreateDispatcher(activityStub, knowledgeStub, handler);
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
                    Summary = "Archivist Wren and Magister Dol argue over the ruin's age.",
                    Involved = ["chars/archivist-wren", "chars/magister-dol"]
                },
                new ActivityChange { CharacterId = "chars/archivist-wren", NewActivity = "Jabbing a finger at the map" },
                new KnowledgeUpdate { CharacterId = "chars/magister-dol", Topic = "Ruin age dispute", Details = "Wren insists it's older than the record shows." }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Discovery_HintsMissingActivityAndKnowledgeUpdate_WhenBatchHasNeither()
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
                    Category = EventCategory.Discovery,
                    Summary = "Party found the hidden stair.",
                    Involved = ["chars/pc1"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.Contains(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Discovery_SuppressesHints_WhenActivityAndKnowledgeUpdatePresent()
    {
        var handler = new EventOccurredHandler();
        var activityStub = new TestHandler(
            "Activity",
            c => c is ActivityChange,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var knowledgeStub = new TestHandler(
            "Knowledge",
            c => c is KnowledgeUpdate,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var dispatcher = CreateDispatcher(activityStub, knowledgeStub, handler);
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
                    Category = EventCategory.Discovery,
                    Summary = "Party found the hidden stair.",
                    Involved = ["chars/pc1"]
                },
                new ActivityChange { CharacterId = "chars/pc1", NewActivity = "Crouching to examine the stair" },
                new KnowledgeUpdate { CharacterId = "chars/pc1", Topic = "Hidden stair", Details = "Found beneath the cellar rug." }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Betrayal_HintsMissingActivityKnowledgeAndRelationship_WhenBatchHasNone()
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
                    Category = EventCategory.Betrayal,
                    Summary = "The steward reveals he sold the party out to the guild.",
                    Involved = ["chars/pc1", "chars/steward"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.Contains(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
        Assert.Contains(result.Summary, s => s.Contains("no 'relationship_change' or 'engagement_relation' found"));
    }

    [Fact]
    public async Task EventFollowUpAdvisor_Betrayal_SuppressesHints_WhenActivityKnowledgeAndRelationshipPresent()
    {
        var handler = new EventOccurredHandler();
        var activityStub = new TestHandler(
            "Activity",
            c => c is ActivityChange,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var knowledgeStub = new TestHandler(
            "Knowledge",
            c => c is KnowledgeUpdate,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var relationshipStub = new TestHandler(
            "Relationship",
            c => c is RelationshipChange,
            (_, _) => Task.FromResult(ChangeHandlerResult.Ok));
        var dispatcher = CreateDispatcher(activityStub, knowledgeStub, relationshipStub, handler);
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
                    Category = EventCategory.Betrayal,
                    Summary = "The steward reveals he sold the party out to the guild.",
                    Involved = ["chars/pc1", "chars/steward"]
                },
                new ActivityChange { CharacterId = "chars/pc1", NewActivity = "Recoiling, hand on sword hilt" },
                new KnowledgeUpdate { CharacterId = "chars/pc1", Topic = "Steward's betrayal", Details = "He sold us out to the guild." },
                new RelationshipChange { CharacterId = "chars/pc1", TargetId = "chars/steward", Delta = -40, Reason = "Betrayed the party to the guild." }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'activity' commit found"));
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'knowledge_update' commit found"));
        Assert.DoesNotContain(result.Summary, s => s.Contains("no 'relationship_change' or 'engagement_relation' found"));
    }

    [Fact]
    public async Task EventNoveltyAdvisor_SkipsSilently_WhenSemanticVectorNotPopulated()
    {
        // Unit tests bypass real persistence (fake logEventAsync delegate), so SemanticVector
        // is never populated. EventNoveltyAdvisor must no-op rather than touch the session.
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
                    Category = EventCategory.Discovery,
                    Summary = "Party found the hidden stair.",
                    Involved = ["chars/pc1"]
                }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Summary, s => s.Contains("reads as novel") || s.Contains("closely echoes"));
    }

    [Fact]
    public async Task DispatchMutationAsync_TracksInvolvedEntities_ForPressureCooldown()
    {
        var hpHandler = new TestHandler("Hp", c => c is HpChange, (c, ctx) =>
        {
            ctx.RecordMessage("HP handled by child dispatch");
            return Task.FromResult(ChangeHandlerResult.Ok);
        });

        var dispatcher = CreateDispatcher(hpHandler);
        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger<WorldChangeDispatcher>.Instance,
            [],
            dispatcher,
            null,
            "test_campaign");

        var targetId = "chars/goblin-42";
        await dispatcher.DispatchMutationAsync(context, new HpChange { CharacterId = targetId, Delta = -3 });

        Assert.Contains(targetId, context.InvolvedEntities);
    }
}
