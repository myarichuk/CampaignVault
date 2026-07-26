using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Grpc;
using CampaignVault.Models;
using CampaignVault.Services;
using Grpc.Core;
using Grpc.Core.Testing;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignSyncServiceTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public CampaignSyncServiceTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    private CampaignSyncService CreateService() => new(_store, new CampaignDocumentKeys());

    private static ServerCallContext CreateContext() => TestServerCallContext.Create(
        method: "test", host: "test", deadline: DateTime.UtcNow.AddMinutes(1), requestHeaders: new Metadata(),
        cancellationToken: CancellationToken.None, peer: "test", authContext: null, contextPropagationToken: null,
        writeHeadersFunc: _ => Task.CompletedTask, writeOptionsGetter: () => new WriteOptions(),
        writeOptionsSetter: _ => { });

    [Fact]
    public async Task GetCampaignEntities_DoesNotLeakOrphanEntities_AcrossCampaigns()
    {
        var service = CreateService();
        var campaignA = "campaign-a-" + Guid.NewGuid();
        var campaignB = "campaign-b-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = "characters/orphan-" + Guid.NewGuid(), Name = "Orphan", CampaignName = null });
            await session.StoreAsync(new Character { Id = "characters/a-" + Guid.NewGuid(), Name = "InA", CampaignName = campaignA });
            await session.SaveChangesAsync();
        }

        var response = await service.GetCampaignEntities(
            new GetCampaignEntitiesRequest { CampaignName = campaignB }, CreateContext());

        Assert.DoesNotContain(response.Entities, e => e.Type == "character");
    }

    [Fact]
    public async Task PushCampaignEntity_UnknownType_ReturnsFailure()
    {
        var service = CreateService();

        var response = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = "test-campaign",
            Id = "widgets/foo",
            Type = "widget",
            Content = "{}"
        }, CreateContext());

        Assert.False(response.Success);
    }

    [Theory]
    [InlineData("customcreature")]
    [InlineData("plotthread")]
    public async Task PushCampaignEntity_ThenGetCampaignEntities_RoundTripsNewEntityTypes(string type)
    {
        var service = CreateService();
        var campaignName = "campaign-" + Guid.NewGuid();
        var id = type == "customcreature" ? "creatures/goblin-" + Guid.NewGuid() : "plotthreads/cult-" + Guid.NewGuid();

        var content = type == "customcreature"
            ? JsonSerializer.Serialize(new CustomCreature { Id = id, Name = "Goblin" })
            : JsonSerializer.Serialize(new PlotThread { Id = id, Title = "The Cult" });

        var pushResponse = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = campaignName,
            Id = id,
            Type = type,
            Content = content
        }, CreateContext());

        Assert.True(pushResponse.Success, pushResponse.Message);

        var listResponse = await service.GetCampaignEntities(
            new GetCampaignEntitiesRequest { CampaignName = campaignName }, CreateContext());

        Assert.Contains(listResponse.Entities, e => e.Id == id && e.Type == type);
    }

    [Fact]
    public async Task PushCampaignEntity_ExistingCanonEntity_IsPushable()
    {
        // Regression: canon entities (CampaignName null/empty) are visible in every campaign per
        // CampaignEntityVisibility.IsVisibleInCampaign, so they aren't "owned" by any one campaign.
        // The ownership check used to compare CampaignName with a plain `!=`, which treated
        // null-vs-"some-campaign" as a mismatch and rejected the push outright.
        var service = CreateService();
        var campaignName = "campaign-" + Guid.NewGuid();
        var id = "characters/canon-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = id, Name = "Canon NPC", CampaignName = null }, id);
            await session.SaveChangesAsync();
        }

        var response = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = campaignName,
            Id = id,
            Type = "character",
            Content = JsonSerializer.Serialize(new Character { Id = id, Name = "Canon NPC Updated" })
        }, CreateContext());

        Assert.True(response.Success, response.Message);
    }

    [Fact]
    public async Task PushCampaignEntity_SameCampaign_UpdatesExistingEntity()
    {
        var service = CreateService();
        var campaignName = "campaign-" + Guid.NewGuid();
        var id = "characters/update-test-" + Guid.NewGuid();

        var firstPush = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = campaignName,
            Id = id,
            Type = "character",
            Content = JsonSerializer.Serialize(new Character { Id = id, Name = "Before" })
        }, CreateContext());
        Assert.True(firstPush.Success, firstPush.Message);

        var secondPush = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = campaignName,
            Id = id,
            Type = "character",
            Content = JsonSerializer.Serialize(new Character { Id = id, Name = "After" })
        }, CreateContext());
        Assert.True(secondPush.Success, secondPush.Message);

        using var verifySession = _store.OpenAsyncSession();
        var updated = await verifySession.LoadAsync<Character>(id);
        Assert.Equal("After", updated!.Name);
    }

    [Fact]
    public async Task PushCampaignEntity_ExistingDifferentCampaign_IsRejected()
    {
        var service = CreateService();
        var ownerCampaign = "owner-campaign-" + Guid.NewGuid();
        var otherCampaign = "other-campaign-" + Guid.NewGuid();
        var id = "characters/owned-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = id, Name = "Owned", CampaignName = ownerCampaign }, id);
            await session.SaveChangesAsync();
        }

        var response = await service.PushCampaignEntity(new PushCampaignEntityRequest
        {
            CampaignName = otherCampaign,
            Id = id,
            Type = "character",
            Content = JsonSerializer.Serialize(new Character { Id = id, Name = "Hijacked" })
        }, CreateContext());

        Assert.False(response.Success);

        using var verifySession = _store.OpenAsyncSession();
        var stillOwned = await verifySession.LoadAsync<Character>(id);
        Assert.Equal(ownerCampaign, stillOwned!.CampaignName);
        Assert.Equal("Owned", stillOwned.Name);
    }

    [Fact]
    public async Task DeleteCampaignEntity_WrongCampaign_DoesNotDelete()
    {
        var service = CreateService();
        var campaignName = "campaign-" + Guid.NewGuid();
        var id = "characters/delete-test-" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = id, Name = "Guarded", CampaignName = campaignName }, id);
            await session.SaveChangesAsync();
        }

        var response = await service.DeleteCampaignEntity(new DeleteCampaignEntityRequest
        {
            CampaignName = "someone-elses-campaign",
            Id = id,
            Type = "character"
        }, CreateContext());

        Assert.False(response.Success);

        using var verifySession = _store.OpenAsyncSession();
        var stillExists = await verifySession.LoadAsync<Character>(id);
        Assert.NotNull(stillExists);
    }
}
