using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ItemDetailHandlerTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ItemDetailHandlerTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Fixed-vector stub: same vector for every call. Fine when similarity isn't under test.</summary>
    private sealed class StubEmbeddingService(float[]? fixedVector = null) : ILocalEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(fixedVector ?? [1f, 0f]);
    }

    /// <summary>Returns caller-configured vectors per exact input text; falls back to a default. Lets tests control cosine similarity precisely.</summary>
    private sealed class MappedEmbeddingService(Dictionary<string, float[]> vectors, float[]? defaultVector = null) : ILocalEmbeddingService
    {
        public int CallCount { get; private set; }

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(vectors.TryGetValue(text, out var v) ? v : defaultVector ?? [0f, 0f, 1f]);
        }
    }

    private sealed class CountingEmbeddingService : ILocalEmbeddingService
    {
        public int CallCount { get; private set; }

        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new float[] { text.Length, 0f });
        }
    }

    private static ChangeContext BuildContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        WorldChangeDispatcher dispatcher,
        string campaignName,
        List<Event>? loggedEvents = null,
        int currentDay = 10) =>
        new(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = currentDay }),
            () => Task.FromResult(new Dictionary<string, string>()),
            e => { loggedEvents?.Add(e); return Task.CompletedTask; },
            [],
            dispatcher,
            campaignName: campaignName);

    private static WorldChangeDispatcher BuildDispatcher(ItemUpdateHandler itemHandler) =>
        new([itemHandler, new KnowledgeUpdateHandler()], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

    [Fact]
    public async Task ApplyAsync_IdMatch_UpdatesExistingDetail_NoDuplicate()
    {
        const string campaign = "item-detail-test-id-match";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item
        {
            Id = "items/id_match_test",
            Name = "Old Chest",
            Description = "A chest.",
            HolderId = "locations/tavern",
            CampaignName = campaign,
            ItemDetails = [new ItemDetail { Id = "detail-existing", Name = "Scratch", Description = "A scratch." }],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var embeddingService = new StubEmbeddingService();
        var handler = new ItemUpdateHandler(embeddingService);
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Id = "detail-existing", Name = "Deep scratch", Description = "A deep scratch." },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.Equal("detail-existing", detail.Id);
        Assert.Equal("Deep scratch", detail.Name);
    }

    [Fact]
    public async Task ApplyAsync_IdMatch_SetsTetheredToId()
    {
        const string campaign = "item-detail-test-tether-set";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item
        {
            Id = "items/tether_set_test",
            Name = "Rope",
            Description = "A length of rope.",
            HolderId = "locations/ruins",
            CampaignName = campaign,
            ItemDetails = [new ItemDetail { Id = "detail-tether", Name = "Lashed end", Description = "Tied off." }],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Id = "detail-tether",
                Name = "Lashed end",
                Description = "Tied off.",
                TetheredToId = "locations/ruins-column",
            },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.Equal("locations/ruins-column", detail.TetheredToId);
    }

    [Fact]
    public async Task ApplyAsync_IdMatch_EmptyStringClearsTetheredToId()
    {
        const string campaign = "item-detail-test-tether-clear";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item
        {
            Id = "items/tether_clear_test",
            Name = "Rope",
            Description = "A length of rope.",
            HolderId = "locations/ruins",
            CampaignName = campaign,
            ItemDetails = [new ItemDetail { Id = "detail-tether", Name = "Lashed end", Description = "Tied off.", TetheredToId = "locations/ruins-column" }],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Id = "detail-tether",
                Name = "Lashed end",
                Description = "Cut free.",
                TetheredToId = "",
            },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.Null(detail.TetheredToId);
    }

    [Fact]
    public async Task ApplyAsync_IdMatch_FailsWhenIdNotFound()
    {
        const string campaign = "item-detail-test-id-not-found";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item { Id = "items/id_not_found_test", Name = "Table", Description = "A table.", HolderId = "locations/tavern", CampaignName = campaign };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Id = "detail-does-not-exist", Name = "X", Description = "Y" },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
        Assert.Empty(item.ItemDetails);
    }

    [Fact]
    public async Task ApplyAsync_SemanticMatch_AboveThreshold_UpdatesExisting()
    {
        const string campaign = "item-detail-test-semantic-above";
        using var session = _fixture.Store.OpenAsyncSession();

        var seed = new ItemDetail
        {
            Id = "detail-seed",
            Name = "Hidden compartment",
            Description = "desc",
            SemanticVector = [1f, 0f],
            EmbeddingTextHash = "stale-hash",
        };
        var item = new Item
        {
            Id = "items/semantic_above_test",
            Name = "Desk",
            Description = "A desk.",
            HolderId = "locations/tavern",
            CampaignName = campaign,
            ItemDetails = [seed],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var probeText = new ItemDetail { Name = "The hidden compartment", Description = "alt desc" }.BuildEmbeddingText();
        // cosine([0.9, 0.4359], [1,0]) ≈ 0.9 — above the handler's 0.86 match threshold.
        var embeddingService = new MappedEmbeddingService(new Dictionary<string, float[]> { [probeText] = [0.9f, 0.4359f] });
        var handler = new ItemUpdateHandler(embeddingService);
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Name = "The hidden compartment", Description = "alt desc" },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.Equal("detail-seed", detail.Id);
        Assert.Equal("The hidden compartment", detail.Name);
    }

    [Fact]
    public async Task ApplyAsync_SemanticMatch_BelowThreshold_CreatesNew()
    {
        const string campaign = "item-detail-test-semantic-below";
        using var session = _fixture.Store.OpenAsyncSession();

        var seed = new ItemDetail
        {
            Id = "detail-seed",
            Name = "Hidden compartment",
            Description = "desc",
            SemanticVector = [1f, 0f],
            EmbeddingTextHash = "stale-hash",
        };
        var item = new Item
        {
            Id = "items/semantic_below_test",
            Name = "Desk",
            Description = "A desk.",
            HolderId = "locations/tavern",
            CampaignName = campaign,
            ItemDetails = [seed],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var probeText = new ItemDetail { Name = "Rusty hinge", Description = "corroded hinge on the trapdoor" }.BuildEmbeddingText();
        // cosine([0,1], [1,0]) == 0 — well below threshold.
        var embeddingService = new MappedEmbeddingService(new Dictionary<string, float[]> { [probeText] = [0f, 1f] });
        var handler = new ItemUpdateHandler(embeddingService);
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Name = "Rusty hinge", Description = "corroded hinge on the trapdoor" },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Equal(2, item.ItemDetails.Count);
        Assert.Contains(item.ItemDetails, d => d.Id == "detail-seed" && d.Name == "Hidden compartment");
        Assert.Contains(item.ItemDetails, d => d.Id != "detail-seed" && d.Name == "Rusty hinge");
    }

    [Fact]
    public async Task ApplyAsync_Retire_SetsFlagAndStatus_KeepsRecord()
    {
        const string campaign = "item-detail-test-retire";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item
        {
            Id = "items/retire_test",
            Name = "Cloak",
            Description = "A cloak.",
            HolderId = "chars/hero",
            CampaignName = campaign,
            ItemDetails = [new ItemDetail { Id = "detail-a", Name = "Stain", Description = "A wine stain." }],
        };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate { ItemId = item.Id, RetireItemDetailId = "detail-a" };
        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var detail = Assert.Single(item.ItemDetails);
        Assert.True(detail.IsRetired);
        Assert.Equal("Retired", detail.Status);
    }

    [Fact]
    public async Task ApplyAsync_Retire_FailsWhenIdNotFound()
    {
        const string campaign = "item-detail-test-retire-not-found";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item { Id = "items/retire_not_found_test", Name = "Cloak", Description = "A cloak.", HolderId = "chars/hero", CampaignName = campaign };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate { ItemId = item.Id, RetireItemDetailId = "detail-nope" };
        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_ParticipantMemoryPush_SetsSourceByRole_AndDedupesOnRepeat()
    {
        const string campaign = "item-detail-test-memory";
        using var session = _fixture.Store.OpenAsyncSession();

        var causer = new Character { Id = "chars/causer", Name = "Causer", CampaignName = campaign };
        var witness = new Character { Id = "chars/witness", Name = "Witness", CampaignName = campaign };
        var item = new Item { Id = "items/memory_test", Name = "Old Table", Description = "A table.", HolderId = "locations/tavern", CampaignName = campaign };
        await session.StoreAsync(causer);
        await session.StoreAsync(witness);
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Name = "Secret glyph",
                Description = "faintly glowing rune",
                Participants =
                [
                    new ItemDetailParticipant { Id = causer.Id, Role = ItemDetailParticipantRole.Caused },
                    new ItemDetailParticipant { Id = witness.Id, Role = ItemDetailParticipantRole.Witnessed },
                ],
            },
        };

        var result = await handler.ApplyAsync(change, context);
        Assert.True(result.Success);

        var detail = Assert.Single(item.ItemDetails);
        var topic = $"itemdetail:{detail.Id}";

        Assert.True(causer.Psychology.Memories.TryGetValue(topic, out var causerMemory));
        Assert.Equal(MemorySource.Experienced, causerMemory!.Source);

        Assert.True(witness.Psychology.Memories.TryGetValue(topic, out var witnessMemory));
        Assert.Equal(MemorySource.Witnessed, witnessMemory!.Source);

        // Repeat upsert of the same detail (by id) should update, not duplicate, each participant's memory.
        var change2 = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Id = detail.Id,
                Name = "Secret glyph",
                Description = "faintly glowing rune, now brighter",
                Participants = [new ItemDetailParticipant { Id = causer.Id, Role = ItemDetailParticipantRole.Caused }],
            },
        };
        await handler.ApplyAsync(change2, context);

        Assert.Single(causer.Psychology.Memories);
        Assert.Contains("brighter", causer.Psychology.Memories[topic].Details);
    }

    [Fact]
    public async Task ApplyAsync_ParentItemEmbedding_RefreshedOnChange_SkippedOnNoOpText()
    {
        const string campaign = "item-detail-test-embedding-refresh";
        using var session = _fixture.Store.OpenAsyncSession();

        var item = new Item { Id = "items/embed_refresh_test", Name = "Lantern", Description = "A lantern.", HolderId = "chars/hero", CampaignName = campaign };
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var embeddingService = new CountingEmbeddingService();
        var handler = new ItemUpdateHandler(embeddingService);
        var context = BuildContext(session, BuildDispatcher(handler), campaign);

        var change1 = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Name = "Soot mark", Description = "black soot on the glass" },
        };
        await handler.ApplyAsync(change1, context);

        Assert.NotNull(item.SemanticVector);
        var callCountAfterFirst = embeddingService.CallCount;
        Assert.True(callCountAfterFirst > 0);

        var detailId = item.ItemDetails.Single().Id;
        var change2 = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest { Id = detailId, Name = "Soot mark", Description = "black soot on the glass" },
        };
        await handler.ApplyAsync(change2, context);

        Assert.Equal(callCountAfterFirst, embeddingService.CallCount);
    }

    [Fact]
    public async Task ApplyAsync_LogsTrivialEvent_WithParticipantsInvolved()
    {
        const string campaign = "item-detail-test-event-log";
        using var session = _fixture.Store.OpenAsyncSession();

        var causer = new Character { Id = "chars/event_causer", Name = "Causer", CampaignName = campaign };
        var item = new Item { Id = "items/event_log_test", Name = "Door", Description = "A door.", HolderId = "locations/tavern", CampaignName = campaign };
        await session.StoreAsync(causer);
        await session.StoreAsync(item);
        await session.SaveChangesAsync();

        var loggedEvents = new List<Event>();
        var handler = new ItemUpdateHandler(new StubEmbeddingService());
        var context = BuildContext(session, BuildDispatcher(handler), campaign, loggedEvents);

        var change = new ItemUpdate
        {
            ItemId = item.Id,
            UpsertItemDetail = new ItemDetailUpsertRequest
            {
                Name = "Scorch mark",
                Description = "black soot",
                Participants = [new ItemDetailParticipant { Id = causer.Id, Role = ItemDetailParticipantRole.Caused }],
            },
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        var loggedEvent = Assert.Single(loggedEvents);
        Assert.Equal(MemoryImportance.Trivial, loggedEvent.Importance);
        Assert.Contains(item.Id, loggedEvent.Involved);
        Assert.Contains(causer.Id, loggedEvent.Involved);
    }
}
