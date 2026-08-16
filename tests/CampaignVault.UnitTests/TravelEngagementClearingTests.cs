using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Records every WorldChange of type T dispatched as a child mutation, so tests can assert on what
/// TravelChangeHandler emits (e.g. the Travel EventOccurred's Details) without a real persistence layer.
/// </summary>
public sealed class SpyHandler<T> : IWorldChangeHandler where T : WorldChange
{
    public List<T> Received { get; } = [];

    public bool ShouldHandle(WorldChange change) => change is T;

    public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        Received.Add((T)change);
        return Task.FromResult(ChangeHandlerResult.Ok);
    }

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null) => change is T;
}

public class TravelEngagementClearingTests
{
    private static (TravelChangeHandler handler, ChangeContext context, Character traveler, SpyHandler<EventOccurred> eventSpy)
        BuildScenario(Dictionary<string, Character> characters, Dictionary<string, Location> locations)
    {
        var travelHandler = new TravelChangeHandler(new EncounterResolver(() => 1.0)); // never interrupts
        var engagementHandler = new EngagementRelationChangeHandler();
        var eventSpy = new SpyHandler<EventOccurred>();

        var dispatcher = new WorldChangeDispatcher(
            [travelHandler, engagementHandler, eventSpy],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance
        );

        var context = ChangeContextTestHelper.Create(
            characters: characters,
            items: new Dictionary<string, Item>(),
            locations: locations,
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            activeCombat: null
        );

        var traveler = characters.Values.First(c => c.CurrentLocationId != null || characters.Count == 1);
        return (travelHandler, context, traveler, eventSpy);
    }

    [Fact]
    public async Task ApplyAsync_ClearsSocialEngagement_WhenTargetLeftBehind()
    {
        var traveler = new Character
        {
            Id = "chars/kaelen",
            Name = "Kaelen",
            CurrentLocationId = "locations/tavern",
            SystemStats = new SystemExtension
            {
                EngagementRelations =
                [
                    new EngagementRelation { TargetId = "chars/bartender", Category = EngagementCategory.Social, Verb = "OrderingDrinksFrom" }
                ]
            }
        };
        var bartender = new Character
        {
            Id = "chars/bartender",
            Name = "Bartender",
            CurrentLocationId = "locations/tavern",
            SystemStats = new SystemExtension
            {
                EngagementRelations = [new EngagementRelation { TargetId = "chars/kaelen", Category = EngagementCategory.Social, Verb = "TalkingWith" }]
            }
        };
        var origin = new Location { Id = "locations/tavern", Name = "Tavern" };
        var destination = new Location { Id = "locations/docks", Name = "Docks" };

        var characters = new Dictionary<string, Character> { [traveler.Id] = traveler, [bartender.Id] = bartender };
        var locations = new Dictionary<string, Location> { [origin.Id] = origin, [destination.Id] = destination };
        var (handler, context, _, eventSpy) = BuildScenario(characters, locations);

        var change = new TravelChange { CharacterId = traveler.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 1.0 };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Empty(traveler.SystemStats!.EngagementRelations);
        Assert.Empty(bartender.SystemStats!.EngagementRelations); // bidirectional clear

        var travelEvent = eventSpy.Received.Single(e => e.Category == EventCategory.Travel);
        Assert.NotNull(travelEvent.Details);
        Assert.True(travelEvent.Details!.ContainsKey("hoursTraveled"));
    }

    [Fact]
    public async Task ApplyAsync_KeepsEngagement_WhenTargetTravelsToSameDestination()
    {
        var traveler = new Character
        {
            Id = "chars/kaelen",
            Name = "Kaelen",
            CurrentLocationId = "locations/tavern",
            SystemStats = new SystemExtension
            {
                EngagementRelations = [new EngagementRelation { TargetId = "chars/companion", Category = EngagementCategory.Social, Verb = "TalkingWith" }]
            }
        };
        var companion = new Character
        {
            Id = "chars/companion",
            Name = "Companion",
            CurrentLocationId = "locations/docks" // already at the destination (e.g. traveled ahead / together)
        };
        var origin = new Location { Id = "locations/tavern", Name = "Tavern" };
        var destination = new Location { Id = "locations/docks", Name = "Docks" };

        var characters = new Dictionary<string, Character> { [traveler.Id] = traveler, [companion.Id] = companion };
        var locations = new Dictionary<string, Location> { [origin.Id] = origin, [destination.Id] = destination };
        var (handler, context, _, _) = BuildScenario(characters, locations);

        var change = new TravelChange { CharacterId = traveler.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 1.0 };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Single(traveler.SystemStats!.EngagementRelations);
    }

    /// <summary>
    /// A party traveling together is expressed as one TravelChange per character in the same take_turn
    /// batch. Whichever character's TravelChange is processed first must NOT sever the relation just
    /// because their companion's own TravelChange (elsewhere in the same batch) hasn't run yet and the
    /// companion is still, in-memory, parked at the origin. Regression test for the batch-order
    /// dependency that ClearStaleEngagementsAsync's context.Batch lookahead fixes.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_KeepsEngagement_WhenCompanionTravelsInSameBatch()
    {
        var traveler = new Character
        {
            Id = "chars/kaelen",
            Name = "Kaelen",
            CurrentLocationId = "locations/tavern",
            SystemStats = new SystemExtension
            {
                EngagementRelations = [new EngagementRelation { TargetId = "chars/companion", Category = EngagementCategory.Social, Verb = "TalkingWith" }]
            }
        };
        var companion = new Character
        {
            Id = "chars/companion",
            Name = "Companion",
            CurrentLocationId = "locations/tavern", // still at the origin — its own TravelChange hasn't run yet
            SystemStats = new SystemExtension
            {
                EngagementRelations = [new EngagementRelation { TargetId = "chars/kaelen", Category = EngagementCategory.Social, Verb = "TalkingWith" }]
            }
        };
        var origin = new Location { Id = "locations/tavern", Name = "Tavern" };
        var destination = new Location { Id = "locations/docks", Name = "Docks" };

        var characters = new Dictionary<string, Character> { [traveler.Id] = traveler, [companion.Id] = companion };
        var locations = new Dictionary<string, Location> { [origin.Id] = origin, [destination.Id] = destination };
        var (handler, context, _, _) = BuildScenario(characters, locations);

        var travelerChange = new TravelChange { CharacterId = traveler.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 1.0 };
        var companionChange = new TravelChange { CharacterId = companion.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 1.0 };

        // Simulate WorldChangeDispatcher's batch: both TravelChanges present, traveler's processed first.
        context.Batch = [travelerChange, companionChange];
        context.BatchIndex = 0;

        var result = await handler.ApplyAsync(travelerChange, context);

        Assert.True(result.Success);
        Assert.Single(traveler.SystemStats!.EngagementRelations);
        Assert.Single(companion.SystemStats!.EngagementRelations);
    }

    [Fact]
    public async Task ApplyAsync_ClearsEngagement_WhenTargetNotLoaded()
    {
        var traveler = new Character
        {
            Id = "chars/kaelen",
            Name = "Kaelen",
            CurrentLocationId = "locations/tavern",
            SystemStats = new SystemExtension
            {
                EngagementRelations = [new EngagementRelation { TargetId = "chars/unloaded-npc", Category = EngagementCategory.Social, Verb = "TalkingWith" }]
            }
        };
        var origin = new Location { Id = "locations/tavern", Name = "Tavern" };
        var destination = new Location { Id = "locations/docks", Name = "Docks" };

        var characters = new Dictionary<string, Character> { [traveler.Id] = traveler };
        var locations = new Dictionary<string, Location> { [origin.Id] = origin, [destination.Id] = destination };
        var (handler, context, _, _) = BuildScenario(characters, locations);

        var change = new TravelChange { CharacterId = traveler.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 1.0 };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Empty(traveler.SystemStats!.EngagementRelations);
    }

    [Fact]
    public async Task ApplyAsync_LogsInterruptedTravelEvent_WithHoursTraveled()
    {
        var traveler = new Character
        {
            Id = "chars/kaelen",
            Name = "Kaelen",
            CurrentLocationId = "locations/tavern"
        };
        var origin = new Location { Id = "locations/tavern", Name = "Tavern" };
        var destination = new Location { Id = "locations/docks", Name = "Docks" };

        var characters = new Dictionary<string, Character> { [traveler.Id] = traveler };
        var locations = new Dictionary<string, Location> { [origin.Id] = origin, [destination.Id] = destination };

        var travelHandler = new TravelChangeHandler(new EncounterResolver(() => 0.0)); // always interrupts
        var eventSpy = new SpyHandler<EventOccurred>();
        var dispatcher = new WorldChangeDispatcher(
            [travelHandler, eventSpy],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance
        );
        var context = ChangeContextTestHelper.Create(
            characters: characters,
            items: new Dictionary<string, Item>(),
            locations: locations,
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            activeCombat: null
        );

        var change = new TravelChange { CharacterId = traveler.Id, DestinationLocationId = destination.Id, TravelCostHoursOverride = 4.0 };
        var result = await travelHandler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var travelEvents = eventSpy.Received.Where(e => e.Category == EventCategory.Travel).ToList();
        Assert.Single(travelEvents);
        Assert.Contains("interrupted", travelEvents[0].Summary, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(travelEvents[0].Details!.ContainsKey("hoursTraveled"));
    }
}
