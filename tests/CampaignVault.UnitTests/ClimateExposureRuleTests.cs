using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ClimateExposureRuleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ClimateExposureRuleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplyAsync_WritesFeltTemperature_AmbientMinusWarmth()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location
        {
            Id = "locations/climate_exposure_temperate_room",
            Name = "Temperate Room",
            ClimateZone = ClimateZone.Temperate,
        };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();

        var character = new Character
        {
            Id = "chars/climate_exposure_warm",
            Name = "Warmly Dressed",
            CurrentLocationId = location.Id,
            SystemStats = new Dnd5eExtension { WarmthRating = 5f },
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { TimeOfDay = TimeOfDay.Noon };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        Assert.Equal(character.Id, delta.CharacterId);
        Assert.Equal("temperature", delta.Attribute);
        Assert.False(delta.IsDelta);
        // Temperate baseline 15 + amplitude 6 * noon multiplier 1.0 = 21; minus warmth 5 = 16.
        Assert.Equal(16f, delta.Value);
    }

    [Fact]
    public async Task ApplyAsync_ExtremeCold_WritesAttributeOnly_NeverAutoAppliesStatusEffect()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location
        {
            Id = "locations/climate_exposure_arctic_night",
            Name = "Arctic Wastes",
            ClimateZone = ClimateZone.Arctic,
        };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();

        var character = new Character
        {
            Id = "chars/climate_exposure_unwarmed",
            Name = "Unwarmed",
            CurrentLocationId = location.Id,
            SystemStats = new Dnd5eExtension(), // WarmthRating defaults to 0
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { TimeOfDay = TimeOfDay.Night };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        // Arctic baseline -20 + amplitude 6 * night multiplier -1.0 = -26; minus warmth 0 = -26 (well below -20 extreme threshold).
        Assert.Equal(-26f, delta.Value);
        Assert.True(delta.Value <= -20f);

        Assert.Empty(result.Deltas.OfType<StatusChange>());
    }

    [Fact]
    public async Task ApplyAsync_CharacterWithoutLocation_Skipped()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = new Character { Id = "chars/climate_exposure_no_location", Name = "Nowhere", SystemStats = new Dnd5eExtension() };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { TimeOfDay = TimeOfDay.Noon };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Deltas);
    }
}
