using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class DeepDiveToolsGetItemTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public DeepDiveToolsGetItemTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetItem_ValidId_ReturnsFullItemWithDetails()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.CreateDeepDiveTools(_fixture, repo);
        var itemId = "items/get-item-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Battered Shield",
                Description = "A dented shield.",
                HolderId = "locations/armory",
                ItemDetails = [new ItemDetailUpsertRequest { Name = "Dent", Description = "A large dent near the rim." }],
            });
            await session.SaveChangesAsync();
        }

        var result = await tools.GetItem(itemId, TestCampaignDefaults.Slug);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Battered Shield", result.Data!.Name);
        Assert.Single(result.Data!.ItemDetails);
        Assert.Contains("1 active detail", result.Summary);
    }

    [Fact]
    public async Task GetItem_BadId_ReturnsNotFound_WithSuggestions()
    {
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.CreateDeepDiveTools(_fixture, repo);
        var itemId = "items/real-item-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertItemAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug), new ItemUpsertRequest
            {
                Id = itemId,
                Name = "Real Item",
                Description = "An item that exists.",
                HolderId = "locations/armory",
            });
            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Item/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetItem("items/nonexistent-" + Guid.NewGuid(), TestCampaignDefaults.Slug);

        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Error);
    }
}
