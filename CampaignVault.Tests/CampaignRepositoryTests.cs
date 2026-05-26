using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Raven.Embedded;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

public class RavenDBFixture : IDisposable
{
    public IDocumentStore Store { get; }
    private readonly string _dataDir;

    public RavenDBFixture()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "RavenDBTest_" + Guid.NewGuid());
        try 
        {
            EmbeddedServer.Instance.StartServer(new ServerOptions
            {
                DataDirectory = _dataDir,
                ServerUrl = "http://127.0.0.1:0"
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already started")) { }

        Store = EmbeddedServer.Instance.GetDocumentStore("TestDB");
        Raven.Client.Documents.Indexes.IndexCreation.CreateIndexes(typeof(CampaignRepository).Assembly, Store);
        Store.Initialize();
    }

    public void Dispose()
    {
        Store.Dispose();
        Thread.Sleep(500);
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}

public class CampaignRepositoryTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public CampaignRepositoryTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    [Fact]
    public async Task GetCharacter_Fuzzy_Match_Works()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();
        
        var id = "npcs/gandalf-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new Character { Id = id, Name = "Gandalf the Grey" });
        await session.SaveChangesAsync();

        // Wait for indexing
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false)) break;
            await Task.Delay(100);
        }

        // Fuzzy match
        var result = await repo.GetCharacterAsync(session, "Gndlf");
        Assert.NotNull(result);
        Assert.Equal("Gandalf the Grey", result!.Name);
    }

    [Fact]
    public async Task Commit_Updates_HP_Delta_Atomically()
    {
        var repo = new CampaignRepository(_store);
        using (var session = _store.OpenAsyncSession())
        {
            var id = "npcs/gimli-" + Guid.NewGuid();
            await repo.UpsertCharacterAsync(session, new Character { Id = id, Name = "Gimli", CurrentHp = 30 });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var id = (await session.Query<Character>().FirstAsync(x => x.Name == "Gimli")).Id;
            await repo.CommitChangesAsync(session, new WorldChange[] { new HpChange { CharacterId = id, Delta = -5 } });
            await session.SaveChangesAsync();
        }

        using (var session = _store.OpenAsyncSession())
        {
            var result = await session.LoadAsync<Character>((await session.Query<Character>().FirstAsync(x => x.Name == "Gimli")).Id);
            Assert.Equal(25, result.CurrentHp);
        }
    }

    [Fact]
    public async Task AdvanceWorld_Is_Atomic_And_Runs_Simulation()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();
        
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(session, new Rumor { Id = "rumors/1", Subject = "Aging Rumor", LastStateChangeDay = 100, RegionLocationId = "loc" });
        await session.SaveChangesAsync();

        var result = await repo.AdvanceWorldAsync(session, 15, TimeOfDay.Noon);
        await session.SaveChangesAsync();

        Assert.Equal(115, result.NewTime.TotalDaysElapsed);
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));
    }

    [Fact]
    public async Task AdvanceWorld_Persists_Simulator_Mutations_On_NPCs_And_Rumors()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();

        // Time + rumor (existing behavior)
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(session, new Rumor { Id = "rumors/sim-1", Subject = "Simulator Persistence Rumor", LastStateChangeDay = 100, RegionLocationId = "loc" });

        // NPC with Schedule (required for simulator load) + Mind.Needs (the mutation target)
        var npcId = "npcs/sim-npc-" + Guid.NewGuid();
        var npc = new Character
        {
            Id = npcId,
            Name = "Simulator Test NPC",
            Schedule = new Schedule
            {
                DefaultLocationId = "loc",
                Routines = new List<Routine> { new Routine { Condition = "Noon", LocationId = "loc", Activity = "Testing" } }
            },
            Mind = new NpcMind
            {
                Needs = new Dictionary<string, int> { ["fatigue"] = 40 }
            }
        };
        await repo.UpsertCharacterAsync(session, npc);
        await session.SaveChangesAsync();

        // Act: advance (simulator mutates in-memory on tracked entities)
        var result = await repo.AdvanceWorldAsync(session, 15, TimeOfDay.Noon);
        await session.SaveChangesAsync();

        // Assert rumor fade (existing)
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));

        // Critical functional assertion: simulator mutations on NPC Mind must survive SaveChanges
        var reloaded = await session.LoadAsync<Character>(npcId);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.Mind);
        Assert.True(reloaded.Mind.Needs.TryGetValue("fatigue", out var fatigueAfter), "fatigue key should exist after simulator run");
        Assert.True(fatigueAfter > 40, "Simulator should have accumulated fatigue over 15 days");
        if (fatigueAfter > 50)
        {
            Assert.Equal("Exhausted", reloaded.Mind.CurrentMood);
        }
    }

    [Fact]
    public async Task GetSceneAsync_WithMissingLocation_ThrowsExpectedException()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();

        var missingId = "locations/does-not-exist-" + Guid.NewGuid();

        // Must not throw raw NullReferenceException from the former location! assertion.
        // A clean, expected exception allows the tool layer to surface a proper error to the LLM.
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            repo.GetSceneAsync(session, missingId));

        Assert.Contains(missingId, ex.Message);
    }

    [Fact]
    public async Task GetWorldState_Aggregates_Context()
    {
        var repo = new CampaignRepository(_store);
        var tools = new CampaignTools(repo);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.SaveTimeAsync(session, new CampaignTime { Day = 10 });
            await repo.LogEventAsync(session, new Event { Id = "e1", Summary = "History", Type = "test", Involved = new List<string> { "loc1" } });
            await repo.UpsertLocationAsync(session, new Location { Id = "loc1", Name = "The Shire", Type = LocationType.Region });
            await session.SaveChangesAsync();
        }

        var result = await tools.GetWorldState("loc1");
        
        Assert.True(result.Success);
        Assert.Equal(10, result.Data!.Time.Day);
        Assert.Equal("The Shire", result.Data.PartyLocation!.Name);
    }

    [Fact]
    public async Task SanitizeValue_Prevents_JsonElement_Leakage()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();
        
        var id = "events/json-test-" + Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new { power = 9001, tags = new[] { "over", "9000" } });
        var details = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        await repo.LogEventAsync(session, new Event { Id = id, Summary = "Power Up", Type = "test", Details = details });
        await session.SaveChangesAsync();

        // Wait for indexing
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Event/Search" && x.IsStale == false)) break;
            await Task.Delay(100);
        }

        var results = await repo.QueryEventsAsync(session, "Power", "test");
        var ev = results.FirstOrDefault(x => x.Id == id);
        Assert.NotNull(ev);
        
        // This should not contain JsonElements
        Assert.IsType<int>(ev.Details!["power"]);
        
        // Final proof: Serialization should work perfectly
        var finalJson = System.Text.Json.JsonSerializer.Serialize(ev);
        Assert.Contains("\"power\":9001", finalJson);
    }

    [Fact]
    public async Task GetNpcContext_Sanitizes_Event_Details_And_Uses_Safe_Query()
    {
        var repo = new CampaignRepository(_store);
        var tools = new CampaignTools(repo);

        using var session = _store.OpenAsyncSession();

        var charId = "npcs/sanitize-npc-" + Guid.NewGuid();
        await repo.UpsertCharacterAsync(session, new Character { Id = charId, Name = "Sanitize NPC" });

        var eventId = "events/npc-involved-" + Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new { secret = 42, tags = new[] { "test" } });
        var details = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        await repo.LogEventAsync(session, new Event
        {
            Id = eventId,
            Summary = "NPC involved event",
            Type = "interaction",
            Involved = new List<string> { charId },
            Details = details
        });
        await session.SaveChangesAsync();

        // Wait for indexing (in case any auto-index is used)
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false)) break;
            await Task.Delay(50);
        }

        var result = await tools.GetNpcContext(charId);

        Assert.True(result.Success);
        var ev = result.Data!.RecentInteractions.FirstOrDefault(e => e.Id == eventId);
        Assert.NotNull(ev);

        // Must be sanitized (no JsonElement leakage)
        Assert.IsType<int>(ev.Details!["secret"]);

        // The query path must not have blown up
        Assert.Contains("NPC involved event", ev.Summary);
    }

    [Fact]
    public async Task V4_Operations_Only_Populate_Mind_Fields_Legacy_TopLevel_Remain_Empty()
    {
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();

        var charId = "npcs/legacy-test-" + Guid.NewGuid();
        var character = new Character
        {
            Id = charId,
            Name = "Legacy Hygiene NPC",
            Mind = new NpcMind
            {
                Relationships = new Dictionary<string, int>(),
                Needs = new Dictionary<string, int> { ["fatigue"] = 5 }
            }
        };
        await repo.UpsertCharacterAsync(session, character);
        await session.SaveChangesAsync();

        // Perform V4 operation that touches Mind (RelationshipChange via Commit)
        await repo.CommitChangesAsync(session, new WorldChange[]
        {
            new RelationshipChange
            {
                SourceId = charId,
                TargetId = "target-1",
                Delta = +10,
                Reason = "Test V4 only path"
            }
        });
        await session.SaveChangesAsync();

        // Reload and verify
        var reloaded = await session.LoadAsync<Character>(charId);
        Assert.NotNull(reloaded);

        // Legacy top-level fields must still be empty (V4 code never touches them)
        Assert.Empty(reloaded.Relationships);      // List<Relationship> legacy
        Assert.Empty(reloaded.KnowledgeGraph);     // List<KnowledgeEdge> legacy
        Assert.Empty(reloaded.Needs);              // Dictionary legacy

        // V4 data lives exclusively in Mind
        Assert.NotNull(reloaded.Mind);
        Assert.True(reloaded.Mind.Relationships.ContainsKey("target-1"));
        Assert.Equal(10, reloaded.Mind.Relationships["target-1"]);
        Assert.True(reloaded.Mind.Needs.ContainsKey("fatigue"));
    }
}
