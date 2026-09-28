using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class PlaneResolverTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public PlaneResolverTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveEffectivePlaneAsync_OwnPlaneSet_ReturnsItDirectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location { Id = "locations/plane_own", Name = "Own Plane", Plane = "Plane of Fire" };

        var plane = await PlaneResolver.ResolveEffectivePlaneAsync(session, location, TestContext.Current.CancellationToken);

        Assert.Equal("Plane of Fire", plane);
    }

    [Fact]
    public async Task ResolveEffectivePlaneAsync_InheritsFromParent()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/plane_inherit_region", Name = "Region", Plane = "Plane of Fire" };
        var settlement = new Location { Id = "locations/plane_inherit_settlement", Name = "Settlement", ParentLocationId = region.Id };
        var room = new Location { Id = "locations/plane_inherit_room", Name = "Room", ParentLocationId = settlement.Id };

        await session.StoreAsync(region, TestContext.Current.CancellationToken);
        await session.StoreAsync(settlement, TestContext.Current.CancellationToken);
        await session.StoreAsync(room, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var plane = await PlaneResolver.ResolveEffectivePlaneAsync(session, room, TestContext.Current.CancellationToken);

        Assert.Equal("Plane of Fire", plane);
    }

    [Fact]
    public async Task ResolveEffectivePlaneAsync_NearestAncestorOverridesFartherOne()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/plane_override_region", Name = "Region", Plane = "Plane of Fire" };
        var settlement = new Location { Id = "locations/plane_override_settlement", Name = "Settlement", ParentLocationId = region.Id, Plane = "Material Plane" };
        var room = new Location { Id = "locations/plane_override_room", Name = "Room", ParentLocationId = settlement.Id };

        await session.StoreAsync(region, TestContext.Current.CancellationToken);
        await session.StoreAsync(settlement, TestContext.Current.CancellationToken);
        await session.StoreAsync(room, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var plane = await PlaneResolver.ResolveEffectivePlaneAsync(session, room, TestContext.Current.CancellationToken);

        Assert.Equal("Material Plane", plane);
    }

    [Fact]
    public async Task ResolveEffectivePlaneAsync_NoneInChain_DefaultsToMaterialPlane()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/plane_default_region", Name = "Region" };
        var room = new Location { Id = "locations/plane_default_room", Name = "Room", ParentLocationId = region.Id };

        await session.StoreAsync(region, TestContext.Current.CancellationToken);
        await session.StoreAsync(room, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var plane = await PlaneResolver.ResolveEffectivePlaneAsync(session, room, TestContext.Current.CancellationToken);

        Assert.Equal(PlaneResolver.MaterialPlane, plane);
    }

    [Fact]
    public async Task ResolveEffectivePlaneAsync_CycleInParentChain_DoesNotHang()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var a = new Location { Id = "locations/plane_cycle_a", Name = "A", ParentLocationId = "locations/plane_cycle_b" };
        var b = new Location { Id = "locations/plane_cycle_b", Name = "B", ParentLocationId = "locations/plane_cycle_a" };

        await session.StoreAsync(a, TestContext.Current.CancellationToken);
        await session.StoreAsync(b, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var plane = await PlaneResolver.ResolveEffectivePlaneAsync(session, a, TestContext.Current.CancellationToken);

        Assert.Equal(PlaneResolver.MaterialPlane, plane);
    }
}
