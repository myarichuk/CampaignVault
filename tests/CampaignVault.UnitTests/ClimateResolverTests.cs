using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ClimateResolverTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ClimateResolverTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveEffectiveZoneAsync_OwnZoneSet_ReturnsItDirectly()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location { Id = "locations/climate_own_zone", Name = "Own Zone", ClimateZone = ClimateZone.Desert };

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, location);

        Assert.Equal(ClimateZone.Desert, zone);
    }

    [Fact]
    public async Task ResolveEffectiveZoneAsync_InheritsFromParent()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/climate_inherit_region", Name = "Region", ClimateZone = ClimateZone.Arctic };
        var settlement = new Location { Id = "locations/climate_inherit_settlement", Name = "Settlement", ParentLocationId = region.Id };
        var room = new Location { Id = "locations/climate_inherit_room", Name = "Room", ParentLocationId = settlement.Id };

        await session.StoreAsync(region);
        await session.StoreAsync(settlement);
        await session.StoreAsync(room);
        await session.SaveChangesAsync();

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, room);

        Assert.Equal(ClimateZone.Arctic, zone);
    }

    [Fact]
    public async Task ResolveEffectiveZoneAsync_NearestAncestorOverridesFartherOne()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/climate_override_region", Name = "Region", ClimateZone = ClimateZone.Arctic };
        var settlement = new Location { Id = "locations/climate_override_settlement", Name = "Settlement", ParentLocationId = region.Id, ClimateZone = ClimateZone.Temperate };
        var room = new Location { Id = "locations/climate_override_room", Name = "Room", ParentLocationId = settlement.Id };

        await session.StoreAsync(region);
        await session.StoreAsync(settlement);
        await session.StoreAsync(room);
        await session.SaveChangesAsync();

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, room);

        Assert.Equal(ClimateZone.Temperate, zone);
    }

    [Fact]
    public async Task ResolveEffectiveZoneAsync_NoneInChain_DefaultsToTemperate()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var region = new Location { Id = "locations/climate_default_region", Name = "Region" };
        var room = new Location { Id = "locations/climate_default_room", Name = "Room", ParentLocationId = region.Id };

        await session.StoreAsync(region);
        await session.StoreAsync(room);
        await session.SaveChangesAsync();

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, room);

        Assert.Equal(ClimateZone.Temperate, zone);
    }

    [Fact]
    public async Task ResolveEffectiveZoneAsync_CycleInParentChain_DoesNotHang()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var a = new Location { Id = "locations/climate_cycle_a", Name = "A", ParentLocationId = "locations/climate_cycle_b" };
        var b = new Location { Id = "locations/climate_cycle_b", Name = "B", ParentLocationId = "locations/climate_cycle_a" };

        await session.StoreAsync(a);
        await session.StoreAsync(b);
        await session.SaveChangesAsync();

        var zone = await ClimateResolver.ResolveEffectiveZoneAsync(session, a);

        Assert.Equal(ClimateZone.Temperate, zone);
    }
}
