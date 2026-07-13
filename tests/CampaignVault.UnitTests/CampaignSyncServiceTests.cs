using System;
using System.Text.Json;
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
        cancellationToken: default, peer: "test", authContext: null, contextPropagationToken: null,
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
