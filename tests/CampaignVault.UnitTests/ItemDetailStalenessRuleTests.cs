using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ItemDetailStalenessRuleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ItemDetailStalenessRuleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Item MakeItem(string id, string campaignName, params ItemDetail[] details) => new()
    {
        Id = id,
        Name = id,
        Description = "An item.",
        HolderId = "locations/tavern",
        CampaignName = campaignName,
        ItemDetails = [.. details],
    };

    [Fact]
    public async Task ApplyAsync_StaleDetail_EmitsNarrative_WithoutMutatingOrDeltas()
    {
        const string campaign = "item-detail-staleness-test-stale";
        using var session = _fixture.Store.OpenAsyncSession();

        var detail = new ItemDetail { Id = "detail-stale", Name = "Old scratch", Description = "desc", UpdatedOnDay = 0 };
        var item = MakeItem("items/staleness_stale", campaign, detail);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new ItemDetailStalenessRule();
        var time = new CampaignTime { TotalDaysElapsed = 61 }; // >= 60-day staleDays threshold
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Contains(result.Narratives, n => n.Text.Contains("Old scratch"));
        Assert.Empty(result.Deltas);
        Assert.Equal(0, detail.UpdatedOnDay); // narrative-only: no mutation of the record itself
        Assert.False(detail.IsRetired);
    }

    [Fact]
    public async Task ApplyAsync_RecentlyUpdatedDetail_EmitsNothing()
    {
        const string campaign = "item-detail-staleness-test-recent";
        using var session = _fixture.Store.OpenAsyncSession();

        var detail = new ItemDetail { Id = "detail-recent", Name = "Fresh scratch", Description = "desc", UpdatedOnDay = 55 };
        var item = MakeItem("items/staleness_recent", campaign, detail);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new ItemDetailStalenessRule();
        var time = new CampaignTime { TotalDaysElapsed = 61 }; // only 6 days since update, below threshold
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Narratives);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task ApplyAsync_ShortReviewInterval_SurfacesBeforeGlobalDefault()
    {
        // A punctured waterskin plausibly changes fast — author sets a 1-day interval, well below
        // the 60-day global default, and it should still surface.
        const string campaign = "item-detail-staleness-test-short-interval";
        using var session = _fixture.Store.OpenAsyncSession();

        var detail = new ItemDetail
        {
            Id = "detail-waterskin", Name = "Punctured waterskin", Description = "desc",
            UpdatedOnDay = 0, ReviewIntervalDays = 1
        };
        var item = MakeItem("items/staleness_short_interval", campaign, detail);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new ItemDetailStalenessRule();
        var time = new CampaignTime { TotalDaysElapsed = 2 }; // 2 days since update, past the 1-day interval but far below the 60-day default
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Contains(result.Narratives, n => n.Text.Contains("Punctured waterskin"));
    }

    [Fact]
    public async Task ApplyAsync_LongReviewInterval_SuppressesPastGlobalDefault()
    {
        // A scorch mark plausibly changes slowly — author sets a 90-day interval, above the 60-day
        // global default, so it should stay quiet even once the default would have fired.
        const string campaign = "item-detail-staleness-test-long-interval";
        using var session = _fixture.Store.OpenAsyncSession();

        var detail = new ItemDetail
        {
            Id = "detail-scorch", Name = "Scorch mark", Description = "desc",
            UpdatedOnDay = 0, ReviewIntervalDays = 90
        };
        var item = MakeItem("items/staleness_long_interval", campaign, detail);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new ItemDetailStalenessRule();
        var time = new CampaignTime { TotalDaysElapsed = 65 }; // past the 60-day default, short of the 90-day interval
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Narratives);
    }

    [Fact]
    public async Task ApplyAsync_RetiredDetail_NeverSurfaced_EvenIfStale()
    {
        const string campaign = "item-detail-staleness-test-retired";
        using var session = _fixture.Store.OpenAsyncSession();

        var detail = new ItemDetail { Id = "detail-retired", Name = "Old stain", Description = "desc", UpdatedOnDay = 0, IsRetired = true };
        var item = MakeItem("items/staleness_retired", campaign, detail);
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var rule = new ItemDetailStalenessRule();
        var time = new CampaignTime { TotalDaysElapsed = 1000 };
        var ctx = new SimulationContext(time, [], [], session, 1, campaign);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Narratives);
        Assert.Empty(result.Deltas);
    }
}
