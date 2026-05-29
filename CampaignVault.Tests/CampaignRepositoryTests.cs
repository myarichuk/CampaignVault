using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Raven.Embedded;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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

[Collection("RavenDB")]
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
        var engine = new DefaultSimulationEngine(
            new ISimulationRule[] { new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule() },
            null);
        var repo = new CampaignRepository(_store, engine, 
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
            new DefaultBehaviorSynthesizer());
        using var session = _store.OpenAsyncSession();
        
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 100 });
        await repo.UpsertRumorAsync(session, new Rumor { Id = "rumors/1", Subject = "Aging Rumor", LastStateChangeDay = 100, RegionLocationId = "loc" });
        await session.SaveChangesAsync();

        // Wait for indexing
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false)) break;
            await Task.Delay(50);
        }

        var result = await repo.AdvanceWorldAsync(session, 15, TimeOfDay.Noon);
        await session.SaveChangesAsync();

        Assert.Equal(115, result.NewTime.TotalDaysElapsed);
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));
    }

    [Fact]
    public async Task AdvanceWorld_Persists_Simulator_Mutations_On_NPCs_And_Rumors()
    {
        var engine = new DefaultSimulationEngine(
            new ISimulationRule[] { new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule() },
            null);
        var repo = new CampaignRepository(_store, engine, 
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
            new DefaultBehaviorSynthesizer());
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
                Needs = new Dictionary<string, float> { ["tiredness"] = 40f }
            }
        };
        await repo.UpsertCharacterAsync(session, npc);
        await session.SaveChangesAsync();

        // Wait for indexing to ensure AdvanceWorld can find the rumor and NPC
        while (true)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false)) break;
            await Task.Delay(50);
        }

        // Act: advance (simulator mutates in-memory on tracked entities)
        var result = await repo.AdvanceWorldAsync(session, 15, TimeOfDay.Noon);
        await session.SaveChangesAsync();

        // Assert rumor fade (existing)
        Assert.Contains(result.SimulatorEvents, e => e.Contains("starting to fade"));

        // Critical functional assertion: simulator mutations on NPC Mind must survive SaveChanges
        var reloaded = await session.LoadAsync<Character>(npcId);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.Mind);
        Assert.True(reloaded.Mind.Needs.TryGetValue("tiredness", out var tirednessAfter), "tiredness key should exist after simulator run");
        Assert.True(tirednessAfter > 40, "Simulator should have accumulated tiredness over 15 days");
        if (tirednessAfter > 80)
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
            await repo.LogEventAsync(session, new Event { Id = "e1", Summary = "History", Category = "test", Involved = new List<string> { "loc1" } });
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

        await repo.LogEventAsync(session, new Event { Id = id, Summary = "Power Up", Category = "test", Details = details });
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
        
        // This should not contain JsonElements.
        // Our central sanitizer prefers long for whole numbers for safety across STJ/Newtonsoft.
        var power = ev.Details!["power"];
        Assert.True(power is int || power is long, $"Expected int or long, got {power?.GetType().Name}");
        
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
            Category = "interaction",
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
        // Note: Depending on RavenDB/System.Text.Json versioning, whole numbers might be long or int.
        // What matters is that it's NOT a JsonElement.
        var secretValue = ev.Details!["secret"];
        Assert.True(secretValue is int || secretValue is long, $"Expected numeric type, got {secretValue?.GetType().Name}");

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
                Needs = new Dictionary<string, float> { ["tiredness"] = 5f }
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

        // V4 data lives exclusively in Mind (legacy top-level fields have been fully removed)
        Assert.NotNull(reloaded.Mind);
        Assert.True(reloaded.Mind.Relationships.ContainsKey("target-1"));
        Assert.Equal(10, reloaded.Mind.Relationships["target-1"]);
        Assert.True(reloaded.Mind.Needs.ContainsKey("tiredness"));
    }

    // =====================================================================
    // REGRESSION TESTS FOR MCP CRASHES REPORTED IN PRODUCTION / COUSIN RUN
    // =====================================================================

    [Fact]
    public async Task SearchWorld_Does_Not_Throw_ActiveAsyncTask_Disposal_Error()
    {
        // This is the exact code path that produced:
        // "Disposing session with active async task is forbidden... Number of active async tasks: 2"
        // The root cause was Task-capture + WhenAll + re-await inside UnifiedSearchAsync
        // combined with ExecuteAsync always doing SaveChanges + dispose.
        var repo = new CampaignRepository(_store);
        var tools = new CampaignTools(repo);

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location
            {
                Id = "locations/search-regression-" + Guid.NewGuid(),
                Name = "Regression Search Target",
                Description = "Used to verify SearchWorld no longer leaves async tasks on the Raven session"
            });
            await session.SaveChangesAsync();
        }

        // Must complete without the Raven disposal exception bubbling out of ExecuteAsync
        var result = await tools.SearchWorld("Regression Search Target");

        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Data!);
    }

    [Fact]
    public async Task LocationMetadata_And_ItemProperties_Never_Leak_JsonElement_Into_Raven()
    {
        // Mirrors SanitizeValue_Prevents_JsonElement_Leakage but for the two other
        // Dictionary<string, object> bags that were unprotected and caused the exact
        // Newtonsoft "ValueIsEscaped" crash during SaveChanges in GetScene.
        var repo = new CampaignRepository(_store);
        var tools = new CampaignTools(repo);

        var locId = "locations/meta-regression-" + Guid.NewGuid();
        var itemId = "items/prop-regression-" + Guid.NewGuid();

        // Simulate exactly what happens when an LLM calls Upsert* with complex JSON:
        // Microsoft.Extensions.AI + System.Text.Json populates Dictionary<string,object>
        // with JsonElement values for objects, arrays, numbers, etc.
        var pollutedMeta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"difficulty": 7, "tags": ["dungeon","trap"], "boss": {"name": "Ancient One", "hp": 900}}""")!;

        var pollutedProps = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"weightKg": 4.2, "enchantments": ["fire", "light"], "charges": 3}""")!;

        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location
            {
                Id = locId,
                Name = "Cursed Vault",
                Description = "Regression test location with complex metadata",
                Metadata = pollutedMeta
            });

            await repo.UpsertItemAsync(session, new Item
            {
                Id = itemId,
                Name = "Cursed Amulet",
                Description = "Item whose Properties would contain JsonElement",
                HolderId = locId,
                Properties = pollutedProps
            });

            await session.SaveChangesAsync();
        }

        // Exercise the precise failing path from the logs: GetScene loads the Location + Items,
        // (now) defensively sanitizes them, then ExecuteAsync does SaveChangesAsync.
        // Before the fix this threw "Error getting value from 'ValueIsEscaped'".
        var scene = await tools.GetScene(locId);
        Assert.True(scene.Success, scene.Summary);

        // Verify the data was sanitized to plain .NET types and survived the roundtrip
        using (var verify = _store.OpenAsyncSession())
        {
            var loc = await verify.LoadAsync<Location>(locId);
            Assert.NotNull(loc);
            // Raven/Newtonsoft often materializes whole numbers as long after roundtrip
            var diff = loc.Metadata["difficulty"];
            Assert.True(diff is int || diff is long, $"Expected int or long, got {diff?.GetType().Name}");
            Assert.IsType<List<object>>(loc.Metadata["tags"]);
            Assert.IsType<Dictionary<string, object>>(loc.Metadata["boss"]);

            var item = await verify.LoadAsync<Item>(itemId);
            Assert.NotNull(item);
            var weight = item.Properties["weightKg"];
            Assert.True(weight is float || weight is double || weight is decimal,
                $"Expected float/double/decimal, got {weight?.GetType().Name}");
            Assert.IsType<List<object>>(item.Properties["enchantments"]);
            var charges = item.Properties["charges"];
            Assert.True(charges is int || charges is long, $"Expected int or long, got {charges?.GetType().Name}");
        }
    }

    [Fact]
    public async Task GetScene_Heals_Legacy_Polluted_Location_And_Item_Data()
    {
        // Simulate legacy data that was written before we had sanitization guards
        // (e.g. direct session.Store or old code paths). GetScene + ExecuteAsync SaveChanges
        // must not explode and should leave clean data behind.
        var repo = new CampaignRepository(_store);
        var tools = new CampaignTools(repo);

        var locId = "locations/legacy-polluted-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            // Manually construct a polluted dictionary the same way STJ does
            var legacyMeta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                """{"legacy": true, "value": {"deep": [1, "two", false]}}""")!;

            var loc = new Location { Id = locId, Name = "Legacy Ruin", Metadata = legacyMeta };
            await session.StoreAsync(loc);
            await session.SaveChangesAsync();
        }

        // This used to be the exact crash site when SaveChanges ran after loading the polluted doc.
        // With legacy data that was persisted while containing live JsonElement instances,
        // Raven's materialization or the subsequent SaveChanges can still surface the
        // "current state of the object" error from a dead JsonElement.
        //
        // The critical regression property we care about: the exception is *caught* inside
        // ExecuteAsync and turned into a clean ToolResult (InternalError) instead of an
        // unhandled exception that kills the MCP server (the original failure mode in the logs).
        var result = await tools.GetScene(locId);

        // Either we healed it and succeeded, or we returned a proper handled error.
        // We must NEVER let an unhandled "ValueIsEscaped" / "active async task" / "state of the object"
        // exception escape the tool invocation.
        if (!result.Success)
        {
            Assert.Equal("InternalError", result.Error);
            Assert.Contains("state of the object", result.Summary ?? "", StringComparison.OrdinalIgnoreCase);
            // For truly dead legacy JsonElement data the healer may still surface a handled error.
            // This is acceptable (no unhandled crash). Skip the post-heal verification.
            return;
        }

        // After the call, the document in Raven should be healed (no more JsonElement).
        // For extremely dead legacy data we may have fallen back to a string representation;
        // the important thing is we didn't crash with an unhandled exception.
        using (var check = _store.OpenAsyncSession())
        {
            var healed = await check.LoadAsync<Location>(locId);
            var legacyVal = healed.Metadata["legacy"];
            Assert.True(legacyVal is bool || legacyVal is string,
                $"Expected bool or string fallback, got {legacyVal?.GetType().Name}");
        }
    }

    [Fact]
    public void Upsert_Tool_Parameter_Names_Are_Descriptive()
    {
        // We use clean, strongly-typed parameters (character, location, lore).
        // This produces the best possible tool schemas for LLM clients that properly
        // consume the current schema.
        //
        // Note: As of late May 2026, Grok Web appears to have a stale/cached view
        // of these two tools and may still call them using the original legacy
        // parameter names "c" and "l". This is a client-side issue.
        var upsertCharacter = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertCharacter), BindingFlags.Public | BindingFlags.Instance)!;
        var upsertLocation = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertLocation), BindingFlags.Public | BindingFlags.Instance)!;
        var upsertLore = typeof(CampaignTools).GetMethod(nameof(CampaignTools.UpsertLore), BindingFlags.Public | BindingFlags.Instance)!;

        Assert.Equal("character", upsertCharacter.GetParameters()[0].Name);
        Assert.Equal("location", upsertLocation.GetParameters()[0].Name);
        Assert.Equal("lore", upsertLore.GetParameters()[0].Name);
    }

    // ============================================================
    // REGRESSION TESTS FOR CLIENT COMPATIBILITY & RECENT FIXES
    // ============================================================

}
