using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase7HandlersTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public Phase7HandlersTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private class CapturingHandler : IWorldChangeHandler
    {
        public List<WorldChange> Captured { get; } = [];
        public bool ShouldHandle(WorldChange change) => change is ActivityChange or NeedChange or EventOccurred;

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
            System.Threading.CancellationToken ct = default)
        {
            Captured.Add(change);
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }

    private ChangeContext CreateTestContext(
        IAsyncDocumentSession session,
        WorldChangeDispatcher dispatcher,
        Character[]? characters = null,
        Location[]? locations = null,
        Faction[]? factions = null,
        Quest[]? quests = null)
    {
        return new ChangeContext(
            session,
            characters?.ToDictionary(c => c.Id) ?? new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            locations?.ToDictionary(l => l.Id) ?? new Dictionary<string, Location>(),
            factions?.ToDictionary(f => f.Id) ?? new Dictionary<string, Faction>(),
            quests?.ToDictionary(q => q.Id) ?? new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { Day = 10, TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            dispatcher,
            null,
            "test-camp"
        );
    }

    [Fact]
    public async Task TravelChange_UpdatesCharacterLocation_AndDestinationLastVisited()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1", CurrentLocationId = "locations/start" };
        var start = new Location { Id = "locations/start", Name = "Start" };
        var dest = new Location { Id = "locations/dest", Name = "Destination" };

        await session.StoreAsync(char1);
        await session.StoreAsync(start);
        await session.StoreAsync(dest);
        await session.SaveChangesAsync();

        var handler = new TravelChangeHandler(new EncounterResolver());
        var capture = new CapturingHandler();
        var dispatcher = new WorldChangeDispatcher([handler, capture], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, [char1], [start, dest]);

        var change = new TravelChange
        {
            CharacterId = char1.Id,
            DestinationLocationId = dest.Id,
            Narrative = "Walked there",
            TravelCostHoursOverride = 2,
            EncounterRiskModifier = -100 // Prevent random encounters during this test
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.Equal(10,
            dest.LastVisitedDay); // Uses TotalDaysElapsed from the test mock time (correct absolute day for eviction logic)

        // Ensure child mutations were emitted (ActivityChange, NeedChange)
        Assert.Contains(capture.Captured,
            m => m is ActivityChange ac && ac.CharacterId == char1.Id && ac.UpdateLocation == true);
        Assert.Contains(capture.Captured,
            m => m is NeedChange nc && nc.CharacterId == char1.Id && nc.Need == "tiredness");
    }

    [Fact]
    public async Task TravelChange_WithoutHoursOverride_UsesExitMetadataForHoursAndTiredness()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1", CurrentLocationId = "locations/start" };
        var start = new Location
        {
            Id = "locations/start",
            Name = "Start",
            // Pre-populate exit metadata so TravelChangeHandler lookup (via context.Locations or fallback Load) resolves cost/terrain
            Exits =
            [
                new LocationExit("locations/dest", "A long wilderness trail", TravelCostHours: 8, Terrain: "wilderness")
            ]
        };
        var dest = new Location { Id = "locations/dest", Name = "Destination" };

        await session.StoreAsync(char1);
        await session.StoreAsync(start);
        await session.StoreAsync(dest);
        await session.SaveChangesAsync();

        var handler = new TravelChangeHandler(new EncounterResolver());
        var capture = new CapturingHandler();
        var dispatcher = new WorldChangeDispatcher([handler, capture], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        // Pass start in the locations dict so TryGetValue succeeds (normal preloaded path after dispatcher preload improvement)
        var ctx = CreateTestContext(session, dispatcher, [char1], [start, dest]);

        var change = new TravelChange
        {
            CharacterId = char1.Id,
            DestinationLocationId = dest.Id,
            Narrative = "Walked there",
            TravelCostHoursOverride = null, // No override -> should lookup from exit
            EncounterRiskModifier = -100 // Prevent random encounters during this test
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        // 8 hours from exit -> (8/4.0f)*10f = 20f tiredness (instead of default 4h=10f)
        Assert.Contains(capture.Captured,
            m => m is NeedChange nc && nc.CharacterId == char1.Id && nc.Need == "tiredness" && nc.Delta == 20f);
    }

    [Fact]
    public async Task TravelChange_WithoutHoursOverride_FallsBackToSessionLoadForOriginExitMetadata()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1", CurrentLocationId = "locations/start" };
        var start = new Location
        {
            Id = "locations/start",
            Name = "Start",
            Exits =
            [
                new LocationExit("locations/dest", "A long wilderness trail", TravelCostHours: 12,
                    Terrain: "wilderness")
            ]
        };
        var dest = new Location { Id = "locations/dest", Name = "Destination" };

        await session.StoreAsync(char1);
        await session.StoreAsync(start);
        await session.StoreAsync(dest);
        await session.SaveChangesAsync();

        var handler = new TravelChangeHandler(new EncounterResolver());
        var capture = new CapturingHandler();
        var dispatcher = new WorldChangeDispatcher([handler, capture], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        // Deliberately omit 'start' from the preloaded locations dict passed to ctx, so the handler's
        // TryGetValue fails and it exercises the Session.LoadAsync fallback for origin exit metadata.
        var ctx = CreateTestContext(session, dispatcher, [char1], [dest]);

        var change = new TravelChange
        {
            CharacterId = char1.Id,
            DestinationLocationId = dest.Id,
            Narrative = "Walked there",
            TravelCostHoursOverride = null, // No override -> should load origin + lookup from exit
            EncounterRiskModifier = -100
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        // 12 hours from exit via fallback load -> (12/4.0f)*10f = 30f tiredness
        Assert.Contains(capture.Captured,
            m => m is NeedChange nc && nc.CharacterId == char1.Id && nc.Need == "tiredness" && nc.Delta == 30f);
    }

    [Fact]
    public async Task FactionCreate_CreatesFaction_AndSetsCampaignName()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var handler = new FactionCreateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher);

        var change = new FactionCreate
        {
            FactionId = "factions/thieves",
            Name = "Thieves Guild",
            Description = "A guild of thieves",
            FactionType = FactionType.Guild,
            ControllingTerritory = "locations/city"
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        var newlyCreated = ctx.Factions.Values.FirstOrDefault();
        Assert.NotNull(newlyCreated);
        Assert.Equal("factions/thieves", newlyCreated!.Id);
        Assert.Equal("test-camp", newlyCreated.CampaignName);
        Assert.Equal("locations/city", newlyCreated.ControllingTerritory);
        Assert.Equal(50, newlyCreated.InfluenceLevel); // Check bug 1 fix (InitialInfluenceLevel fallback)

        // Regression test for Bug 2: ensure it was persisted
        await session.SaveChangesAsync();
        using var readSession = _fixture.Store.OpenAsyncSession();
        var fromDb = await readSession.LoadAsync<Faction>("factions/thieves");
        Assert.NotNull(fromDb);
    }

    [Fact]
    public async Task FactionReputationChange_UpdatesCharacterSocialProfile()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1" };
        var faction = new Faction { Id = "factions/thieves" };

        var handler = new FactionReputationChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, [char1], null, [faction]);

        var change = new FactionReputationChange
        {
            CharacterId = char1.Id,
            FactionId = faction.Id,
            Delta = 15,
            Reason = "Helped the boss"
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.True(char1.Social.FactionReputations.ContainsKey(faction.Id));
        Assert.Equal(15, char1.Social.FactionReputations[faction.Id]);
    }

    [Fact]
    public async Task FactionStateChange_UpdatesStanceAndInfluence()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var faction = new Faction { Id = "factions/thieves", InfluenceLevel = 50 };
        var targetFaction = new Faction { Id = "factions/guards" };

        var handler = new FactionStateChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, [faction, targetFaction]);

        var change = new FactionStateChange
        {
            FactionId = faction.Id,
            TargetFactionId = targetFaction.Id,
            NewStance = FactionStance.Hostile,
            InfluenceDelta = -5
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.Equal(45, faction.InfluenceLevel);
        Assert.True(faction.StanceToward.ContainsKey(targetFaction.Id));
        Assert.Equal(FactionStance.Hostile, faction.StanceToward[targetFaction.Id]);
    }

    [Fact]
    public async Task QuestCreate_CreatesQuest_AndSetsCampaignName()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var handler = new QuestCreateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher);

        var change = new QuestCreate
        {
            QuestId = "quests/rats_01",
            Title = "Clear the Rats",
            GiverId = "characters/bram",
            Objectives = [new QuestObjectiveDto { Description = "Kill rats" }]
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        var newlyCreated = ctx.Quests.Values.FirstOrDefault();
        Assert.NotNull(newlyCreated);
        Assert.Equal("quests/rats_01", newlyCreated!.Id);
        Assert.Equal("test-camp", newlyCreated.CampaignName);
        Assert.Equal(10, newlyCreated.LastUpdatedDay);
        Assert.Single(newlyCreated.Objectives);
        Assert.Equal("Kill rats", newlyCreated.Objectives[0].Description);

        // Regression test for Bug 2: ensure it was persisted
        await session.SaveChangesAsync();
        using var readSession = _fixture.Store.OpenAsyncSession();
        var fromDb = await readSession.LoadAsync<Quest>("quests/rats_01");
        Assert.NotNull(fromDb);
    }

    [Fact]
    public async Task QuestProgress_UpdatesObjective_AndEmitsEventOnComplete()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var quest = new Quest
        {
            Id = "quests/rats_01",
            Title = "Clear the Rats",
            OverallState = QuestState.Open,
            Objectives = [new QuestObjective("Kill rats", QuestState.Open)]
        };

        var handler = new QuestProgressHandler();
        var capture = new CapturingHandler();
        var dispatcher = new WorldChangeDispatcher([handler, capture], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, null, [quest]);

        var change = new QuestProgress
        {
            QuestId = quest.Id,
            ObjectiveIndex = 0,
            NewState = QuestState.Complete,
            NarrativeNote = "Rats are dead"
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.Equal(QuestState.Complete, quest.Objectives[0].State);
        Assert.Equal(10, quest.Objectives[0].DayCompleted);
        Assert.Equal(10, quest.Objectives[0].DayStarted); // Open → Complete anchors both timestamps
        Assert.Equal(10, quest.LastUpdatedDay);

        // Since all objectives are complete, OverallState should be Complete
        Assert.Equal(QuestState.Complete, quest.OverallState);

        // Ensure EventOccurred child mutation was emitted and category is Discovery (Issue 3)
        Assert.Contains(capture.Captured,
            m => m is EventOccurred e && e.Summary.Contains("Clear the Rats") && e.Category == EventCategory.Discovery);
    }

    [Fact]
    public async Task QuestProgress_UsesTotalDaysElapsed_NotCalendarDay()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var quest = new Quest
        {
            Id = "quests/elapsed_01",
            Title = "Long Campaign Quest",
            OverallState = QuestState.Open,
            Objectives = [new QuestObjective("Do the thing", QuestState.Open)]
        };

        var handler = new QuestProgressHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest> { [quest.Id] = quest },
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { Day = 11, TotalDaysElapsed = 400 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            dispatcher,
            null,
            "test-camp"
        );

        var result = await handler.ApplyAsync(new QuestProgress
        {
            QuestId = quest.Id,
            ObjectiveIndex = 0,
            NewState = QuestState.Complete
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal(400, quest.Objectives[0].DayCompleted);
        Assert.Equal(400, quest.Objectives[0].DayStarted);
        Assert.Equal(400, quest.LastUpdatedDay);
        Assert.NotEqual(11, quest.Objectives[0].DayCompleted); // calendar Day must not leak into timestamps
    }

    [Fact]
    public async Task QuestProgress_WithoutTarget_FailsWithClearMessage()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var quest = new Quest
            { Id = "quests/rats_01", Objectives = [new QuestObjective("Kill rats", QuestState.Open)] };
        var handler = new QuestProgressHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, null, [quest]);

        var change = new QuestProgress
        {
            QuestId = quest.Id,
            NewState = QuestState.Complete,
            // ObjectiveIndex and ObjectiveName are both missing
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.False(result.Success);
        Assert.Contains("Must specify either", result.Message ?? "");
    }

    [Fact]
    public async Task QuestProgress_RevertsDayCompleted_WhenReopened()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var quest = new Quest
        {
            Id = "quests/rats_01",
            Objectives = [new QuestObjective("Kill rats", QuestState.Complete) { DayCompleted = 5 }]
        };
        var handler = new QuestProgressHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, null, [quest]);

        var change = new QuestProgress
        {
            QuestId = quest.Id,
            ObjectiveIndex = 0,
            NewState = QuestState.Open
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);
        Assert.Null(quest.Objectives[0].DayCompleted); // Regression for bug 4
        Assert.Equal(QuestState.Open, quest.OverallState); // OverallState must be downgraded too
    }

    [Fact]
    public async Task FactionStateChange_WithNullStanceAndNullInfluence_RecordsWarningAndSucceeds()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var faction = new Faction { Id = "factions/thieves", InfluenceLevel = 50 };
        var handler = new FactionStateChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var summary = new List<string>();
        var ctx = new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction> { [faction.Id] = faction },
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { Day = 10, TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            summary,
            dispatcher,
            null,
            "test-camp"
        );

        var change = new FactionStateChange
        {
            FactionId = faction.Id,
            NewStance = null,
            InfluenceDelta = null
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);
        Assert.Equal(50, faction.InfluenceLevel); // Unchanged
        Assert.Contains(summary, m => m.Contains("no stance or influence delta specified"));
    }

    [Fact]
    public async Task FactionReputationChange_ClampsAtBoundaries()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1" };
        char1.Social.FactionReputations["factions/thieves"] = 95;
        var faction = new Faction { Id = "factions/thieves", Name = "Thieves Guild" };

        var handler = new FactionReputationChangeHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, [char1], null, [faction]);

        // Delta of +20 from 95 would give 115 — must be clamped to 100
        var change = new FactionReputationChange
        {
            CharacterId = char1.Id,
            FactionId = faction.Id,
            Delta = 20
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);
        Assert.Equal(100, char1.Social.FactionReputations[faction.Id]);

        // Now test negative clamping
        char1.Social.FactionReputations[faction.Id] = -90;
        var change2 = new FactionReputationChange
        {
            CharacterId = char1.Id,
            FactionId = faction.Id,
            Delta = -20
        };
        var result2 = await handler.ApplyAsync(change2, ctx);
        Assert.True(result2.Success);
        Assert.Equal(-100, char1.Social.FactionReputations[faction.Id]);
    }

    [Fact]
    public async Task QuestProgress_PartialCompletion_SetsOverallStateToInProgress()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var quest = new Quest
        {
            Id = "quests/multipart",
            Title = "A Long Journey",
            OverallState = QuestState.Open,
            Objectives =
            [
                new QuestObjective("Find the map", QuestState.Open),
                new QuestObjective("Cross the mountains", QuestState.Open)
            ]
        };

        var handler = new QuestProgressHandler();
        var capture = new CapturingHandler();
        var dispatcher = new WorldChangeDispatcher([handler, capture], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, null, [quest]);

        // Complete only the first objective
        var change = new QuestProgress
        {
            QuestId = quest.Id,
            ObjectiveIndex = 0,
            NewState = QuestState.Complete
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.Equal(QuestState.Complete, quest.Objectives[0].State);
        Assert.Equal(QuestState.Open, quest.Objectives[1].State);
        Assert.Equal(QuestState.InProgress, quest.OverallState); // Not all done — must be InProgress

        // No EventOccurred should be emitted (quest not yet complete or failed)
        Assert.DoesNotContain(capture.Captured, m => m is EventOccurred);
    }

    [Fact]
    public async Task TravelChange_ToInvalidDestination_ReturnsFailureWithSuggestion()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var char1 = new Character { Id = "characters/pc1", CurrentLocationId = "locations/start" };
        var start = new Location { Id = "locations/start", Name = "Start" };

        var handler = new TravelChangeHandler(new EncounterResolver());
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, [char1], [start]);
        // Note: "locations/dest" is NOT in context

        var change = new TravelChange
        {
            CharacterId = char1.Id,
            DestinationLocationId = "locations/dest",
            Narrative = "Walked there"
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.False(result.Success);
        Assert.Contains("locations/dest", result.Message ?? "");
        // Character location unchanged
        Assert.Equal("locations/start", char1.CurrentLocationId);
    }

    [Fact]
    public async Task FactionCreate_DuplicateFactionId_ReturnsFailure()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var existing = new Faction { Id = "factions/thieves", Name = "Thieves Guild", CampaignName = "test-camp" };
        var handler = new FactionCreateHandler();
        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = CreateTestContext(session, dispatcher, null, null, [existing]);

        var change = new FactionCreate
        {
            FactionId = "factions/thieves", // Same ID
            Name = "Impostors",
            FactionType = FactionType.Guild
        };

        var result = await handler.ApplyAsync(change, ctx);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message ?? "");
    }
}