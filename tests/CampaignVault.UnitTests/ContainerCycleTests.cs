using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ContainerCycleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ContainerCycleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Item MakeContainer(string id, string holderId, int? capacity = null) => new()
    {
        Id = id,
        Name = id,
        HolderId = holderId,
        CoreCategory = ItemCategory.Container,
        Capacity = capacity,
        CampaignName = "container-test",
    };

    [Fact]
    public async Task ValidateNestingAsync_DirectCycle_Fails()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var backpack = MakeContainer("items/cycle_direct_backpack", "chars/hero");
        // The pouch is already inside the backpack.
        var pouch = MakeContainer("items/cycle_direct_pouch", backpack.Id);

        await session.StoreAsync(backpack);
        await session.StoreAsync(pouch);
        await session.SaveChangesAsync();

        // Now try to move the backpack into the pouch it already contains.
        var error = await ContainerResolver.ValidateNestingAsync(session, backpack, pouch);

        Assert.NotNull(error);
        Assert.Contains("cycle", error);
    }

    [Fact]
    public async Task ValidateNestingAsync_IndirectCycle_Fails()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var backpack = MakeContainer("items/cycle_indirect_backpack", "chars/hero");
        var pouch = MakeContainer("items/cycle_indirect_pouch", backpack.Id);
        var vial = MakeContainer("items/cycle_indirect_vial", pouch.Id);

        await session.StoreAsync(backpack);
        await session.StoreAsync(pouch);
        await session.StoreAsync(vial);
        await session.SaveChangesAsync();

        // backpack -> pouch -> vial; try to move the backpack into the vial (two levels down).
        var error = await ContainerResolver.ValidateNestingAsync(session, backpack, vial);

        Assert.NotNull(error);
        Assert.Contains("cycle", error);
    }

    [Fact]
    public async Task ValidateNestingAsync_DepthLimitExceeded_Fails()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        // Build a chain of 10 nested containers, none of which is the moving item.
        Item? previous = null;
        for (var i = 0; i < 10; i++)
        {
            var holderId = previous?.Id ?? "chars/hero";
            var container = MakeContainer($"items/cycle_depth_c{i}", holderId);
            await session.StoreAsync(container);
            previous = container;
        }
        await session.SaveChangesAsync();

        var deepest = previous!;
        var movingItem = MakeContainer("items/cycle_depth_mover", "chars/hero");
        await session.StoreAsync(movingItem);
        await session.SaveChangesAsync();

        var error = await ContainerResolver.ValidateNestingAsync(session, movingItem, deepest);

        Assert.NotNull(error);
        Assert.Contains("depth", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateNestingAsync_CapacityExceeded_Fails()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var pouch = MakeContainer("items/cycle_capacity_pouch", "chars/hero", capacity: 2);
        var coin1 = new Item { Id = "items/cycle_capacity_coin1", Name = "Coin", HolderId = pouch.Id, Quantity = 1, CampaignName = "container-test" };
        var coin2 = new Item { Id = "items/cycle_capacity_coin2", Name = "Coin", HolderId = pouch.Id, Quantity = 1, CampaignName = "container-test" };
        var coin3 = new Item { Id = "items/cycle_capacity_coin3", Name = "Coin", HolderId = "chars/hero", Quantity = 1, CampaignName = "container-test" };

        await session.StoreAsync(pouch);
        await session.StoreAsync(coin1);
        await session.StoreAsync(coin2);
        await session.StoreAsync(coin3);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var error = await ContainerResolver.ValidateNestingAsync(session, coin3, pouch);

        Assert.NotNull(error);
        Assert.Contains("capacity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateNestingAsync_ValidNesting_ReturnsNull()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var pouch = MakeContainer("items/cycle_valid_pouch", "chars/hero", capacity: 5);
        var coin = new Item { Id = "items/cycle_valid_coin", Name = "Coin", HolderId = "chars/hero", Quantity = 1, CampaignName = "container-test" };

        await session.StoreAsync(pouch);
        await session.StoreAsync(coin);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var error = await ContainerResolver.ValidateNestingAsync(session, coin, pouch);

        Assert.Null(error);
    }
}
