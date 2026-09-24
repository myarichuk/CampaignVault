using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data.Migrations;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class MigratePointsOfInterestTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public MigratePointsOfInterestTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    [Fact]
    public async Task DetailedPoisBecomeFixtureItems_NameOnlyPoisAreDropped_AndItIsIdempotent()
    {
        var campaign = "poi-migration-" + Guid.NewGuid().ToString("N")[..8];
        var withDescription = $"campaigns/{campaign}/locations/tavern";
        var bare = $"campaigns/{campaign}/locations/cellar";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Location
            {
                Id = withDescription,
                Name = "Tavern",
                Description = "A smoky tavern.",
                CampaignName = campaign,
                PointsOfInterest = ["Notice Board", "Hearth"],
                PointOfInterestDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Notice Board"] = "Curling bounty posters."
                },
                PoisUsedByActivity = ["Hearth"]
            }, withDescription);
            await session.StoreAsync(new Location
            {
                Id = bare,
                Name = "Cellar",
                Description = "",
                CampaignName = campaign,
                PointsOfInterest = ["Wine racks", "Trapdoor"]
            }, bare);
            await session.SaveChangesAsync();
        }

        var migration = new MigratePointsOfInterestToFixtures(_store);
        var (locations, items) = await migration.ExecuteAsync();
        Assert.True(locations >= 2);
        Assert.True(items >= 1);

        using (var session = _store.OpenAsyncSession())
        {
            var tavern = await session.LoadAsync<Location>(withDescription);
            Assert.Empty(tavern!.PointsOfInterest);
            Assert.Empty(tavern.PointOfInterestDetails);
            Assert.Empty(tavern.PoisUsedByActivity);
            Assert.Equal("A smoky tavern.", tavern.Description);

            var fixture = await session.LoadAsync<Item>($"campaigns/{campaign}/items/tavern-notice-board");
            Assert.NotNull(fixture);
            Assert.Equal(withDescription, fixture!.HolderId);
            Assert.Equal("Curling bounty posters.", fixture.Description);
            Assert.Contains("fixture", fixture.Tags);

            var cellar = await session.LoadAsync<Location>(bare);
            Assert.Equal("Notable features: Wine racks, Trapdoor.", cellar!.Description);
        }

        var (again, createdAgain) = await migration.ExecuteAsync();
        Assert.Equal(0, again);
        Assert.Equal(0, createdAgain);
    }
}
