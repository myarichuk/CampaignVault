using CampaignVault.Data;
using CampaignVault.Models;
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
        EmbeddedServer.Instance.StartServer(new ServerOptions
        {
            DataDirectory = _dataDir,
            ServerUrl = "http://127.0.0.1:0"
        });
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
    public async Task Can_Upsert_And_Get_Character_With_KnowledgeGraph_And_Needs()
    {
        using var repository = new CampaignRepository(_store);
        var character = new Character
        {
            Id = "npcs/gandalf-" + Guid.NewGuid(),
            Name = "Gandalf",
            ClassLevel = "Wizard 20",
            CurrentHp = 100,
            MaxHp = 100,
            Needs = new Dictionary<string, int> { { "hunger", 10 }, { "thirst", 5 } },
            KnowledgeGraph = new List<KnowledgeEdge> 
            { 
                new KnowledgeEdge("locations/shire", "Frequent visitor") 
            }
        };

        await repository.UpsertCharacterAsync(character);
        var result = await repository.GetCharacterAsync(character.Id);

        Assert.NotNull(result);
        Assert.Equal("Gandalf", result.Name);
        Assert.Equal(10, result.Needs["hunger"]);
        Assert.Single(result.KnowledgeGraph);
    }

    [Fact]
    public async Task Fuzzy_Search_Lore_Works()
    {
        using var repository = new CampaignRepository(_store);
        var id = "lore/the-one-ring-" + Guid.NewGuid();
        await repository.UpsertLoreAsync(new Lore
        {
            Id = id,
            Title = "The One Ring",
            Content = "A powerful artifact created by Sauron."
        });

        // Wait for indexing
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Lore/Search" && x.IsStale == false))
                break;
            await Task.Delay(100);
        }
        
        var results = await repository.QueryLoreAsync("Rng", null, null);

        Assert.NotEmpty(results);
        Assert.Contains(results, x => x.Id == id);
    }

    [Fact]
    public async Task Fuzzy_Search_Character_Works()
    {
        using var repository = new CampaignRepository(_store);
        var id = "npcs/aragorn-" + Guid.NewGuid();
        await repository.UpsertCharacterAsync(new Character
        {
            Id = id,
            Name = "Aragorn",
            Notes = "The rightful King of Gondor."
        });

        // Wait for indexing
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
                break;
            await Task.Delay(100);
        }

        // Try fuzzy name match
        var result = await repository.GetCharacterAsync("Aragrn");

        Assert.NotNull(result);
        Assert.Equal("Aragorn", result!.Name);
    }

    [Fact]
    public async Task Optimistic_Concurrency_Prevents_Drift()
    {
        using var repository = new CampaignRepository(_store);
        var id = "npcs/bilbo-" + Guid.NewGuid();
        var character = new Character { Id = id, Name = "Bilbo", CurrentHp = 20 };
        await repository.UpsertCharacterAsync(character);

        using var session1 = _store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes });
        using var session2 = _store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes });

        var char1 = await session1.LoadAsync<Character>(id);
        var char2 = await session2.LoadAsync<Character>(id);

        char1.CurrentHp = 15;
        await session1.SaveChangesAsync();

        char2.CurrentHp = 10;
        await Assert.ThrowsAsync<Raven.Client.Exceptions.ConcurrencyException>(() => session2.SaveChangesAsync());
    }
}
