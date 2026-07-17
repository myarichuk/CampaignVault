using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ClimateShiftPressureContributorTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ClimateShiftPressureContributorTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<(Location location, Character pc)> SeedAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        string campaign, ClimateZone zone, float warmthRating)
    {
        var location = new Location
        {
            Id = $"locations/climate_shift_{Guid.NewGuid():N}",
            Name = "Test Location",
            ClimateZone = zone,
            CampaignName = campaign,
        };
        var pc = new Character
        {
            Id = $"chars/climate_shift_{Guid.NewGuid():N}",
            Name = "Traveler",
            CurrentLocationId = location.Id,
            IsPc = true,
            CampaignName = campaign,
            SystemStats = new Dnd5eExtension { WarmthRating = warmthRating },
        };

        await session.StoreAsync(location);
        await session.StoreAsync(pc);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        return (location, pc);
    }

    [Fact]
    public async Task EvaluateAsync_UnderdressedInArctic_EmitsGearMismatchPressure()
    {
        const string campaign = "climate-shift-cold";
        using var session = _fixture.Store.OpenAsyncSession();
        var (location, pc) = await SeedAsync(session, campaign, ClimateZone.Arctic, warmthRating: 0f);

        var contributor = new ClimateShiftPressureContributor();
        var ctx = new PressureContext(
            campaign,
            new CampaignTime { TimeOfDay = TimeOfDay.Night },
            new CampaignConfig { Id = "config/" + campaign },
            session,
            RequestedLocationId: location.Id,
            PartyPresent: true);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        var pressure = Assert.Single(pressures.Where(p => p.EntityId == pc.Id));
        Assert.Contains("underdressed", pressure.Text);
        Assert.Equal(ClimateShiftPressureContributor.GroupingKey, pressure.GroupingKey);
    }

    [Fact]
    public async Task EvaluateAsync_WellDressedForClimate_NoPressure()
    {
        const string campaign = "climate-shift-comfortable";
        using var session = _fixture.Store.OpenAsyncSession();
        // Temperate at Noon = 15 + 6 = 21; warmth 3 => felt 18, exactly the comfortable reference.
        var (location, pc) = await SeedAsync(session, campaign, ClimateZone.Temperate, warmthRating: 3f);

        var contributor = new ClimateShiftPressureContributor();
        var ctx = new PressureContext(
            campaign,
            new CampaignTime { TimeOfDay = TimeOfDay.Noon },
            new CampaignConfig { Id = "config/" + campaign },
            session,
            RequestedLocationId: location.Id,
            PartyPresent: true);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        Assert.DoesNotContain(pressures, p => p.EntityId == pc.Id);
    }

    [Fact]
    public async Task EvaluateAsync_PartyNotPresent_NoPressure()
    {
        const string campaign = "climate-shift-no-party";
        using var session = _fixture.Store.OpenAsyncSession();
        var (location, pc) = await SeedAsync(session, campaign, ClimateZone.Arctic, warmthRating: 0f);

        var contributor = new ClimateShiftPressureContributor();
        var ctx = new PressureContext(
            campaign,
            new CampaignTime { TimeOfDay = TimeOfDay.Night },
            new CampaignConfig { Id = "config/" + campaign },
            session,
            RequestedLocationId: location.Id,
            PartyPresent: false);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        Assert.DoesNotContain(pressures, p => p.EntityId == pc.Id);
    }
}
