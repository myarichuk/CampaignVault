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
}
