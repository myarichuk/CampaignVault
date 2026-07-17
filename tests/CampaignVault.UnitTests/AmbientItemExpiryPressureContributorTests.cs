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
public class AmbientItemExpiryPressureContributorTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public AmbientItemExpiryPressureContributorTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EvaluateAsync_SurfacedItem_EmitsPressureQuotingNote()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var item = new Item
        {
            Id = "items/ambient_pressure_surfaced",
            Name = "Cold Porridge Bowl",
            Description = "A bowl of porridge, gone cold.",
            HolderId = "locations/tavern",
            CampaignName = "ambient-pressure-test",
            Persistence = new AmbientPersistence
            {
                Note = "left on the tavern table after breakfast",
                ExpiresAtDay = 3,
                PressureSurfaced = true,
            },
        };
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var contributor = new AmbientItemExpiryPressureContributor();
        var ctx = new PressureContext(
            "ambient-pressure-test",
            new CampaignTime { TotalDaysElapsed = 10 },
            new CampaignConfig { Id = "config/ambient-pressure-test" },
            session);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        var pressure = Assert.Single(pressures.Where(p => p.EntityId == item.Id));
        Assert.Contains("left on the tavern table after breakfast", pressure.Text);
        Assert.Equal(AmbientItemExpiryPressureContributor.GroupingKey, pressure.GroupingKey);
    }

    [Fact]
    public async Task EvaluateAsync_NotYetSurfacedItem_NoPressure()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var item = new Item
        {
            Id = "items/ambient_pressure_not_surfaced",
            Name = "Fresh Bread",
            Description = "Still warm.",
            HolderId = "locations/tavern",
            CampaignName = "ambient-pressure-test",
            Persistence = new AmbientPersistence { Note = "fresh off the oven", ExpiresAtDay = 100, PressureSurfaced = false },
        };
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var contributor = new AmbientItemExpiryPressureContributor();
        var ctx = new PressureContext(
            "ambient-pressure-test",
            new CampaignTime { TotalDaysElapsed = 10 },
            new CampaignConfig { Id = "config/ambient-pressure-test" },
            session);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        Assert.DoesNotContain(pressures, p => p.EntityId == item.Id);
    }

    [Fact]
    public async Task EvaluateAsync_NoPersistence_NoPressure()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var item = new Item
        {
            Id = "items/ambient_pressure_no_persistence",
            Name = "Regular Sword",
            Description = "A sword.",
            HolderId = "chars/hero",
            CampaignName = "ambient-pressure-test",
        };
        await session.StoreAsync(item);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync();

        var contributor = new AmbientItemExpiryPressureContributor();
        var ctx = new PressureContext(
            "ambient-pressure-test",
            new CampaignTime { TotalDaysElapsed = 10 },
            new CampaignConfig { Id = "config/ambient-pressure-test" },
            session);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();

        Assert.DoesNotContain(pressures, p => p.EntityId == item.Id);
    }
}
