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
    public async Task ApplyAsync_WritesFeltTemperature_AmbientPlusWarmth()
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
            IsPc = true, // PCs are always in exposure scope; these tests are about the arithmetic
            SystemStats = new Dnd5eExtension { WarmthRating = 5f },
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 12 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        Assert.Equal(character.Id, delta.CharacterId);
        Assert.Equal("temperature", delta.Attribute);
        Assert.False(delta.IsDelta);
        // Temperate baseline 15 + amplitude 6 * noon multiplier 1.0 = 21; plus warmth 5 = 26.
        Assert.Equal(26f, delta.Value);
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
            IsPc = true, // PCs are always in exposure scope; these tests are about the arithmetic
            SystemStats = new Dnd5eExtension(), // WarmthRating defaults to 0
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 0 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        // Arctic baseline -20 + amplitude 6 * night multiplier -1.0 = -26; plus warmth 0 = -26 (well below -20 extreme threshold).
        Assert.Equal(-26f, delta.Value);
        Assert.True(delta.Value <= -20f);

        Assert.Empty(result.Deltas.OfType<StatusChange>());
    }

    [Fact]
    public async Task ApplyAsync_FursInArctic_WarmsFeltTemperature_NoLongerExtreme()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location
        {
            Id = "locations/climate_exposure_arctic_furs",
            Name = "Arctic Camp",
            ClimateZone = ClimateZone.Arctic,
        };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();

        var character = new Character
        {
            Id = "chars/climate_exposure_furs_arctic",
            Name = "Furred Traveler",
            CurrentLocationId = location.Id,
            IsPc = true, // PCs are always in exposure scope; these tests are about the arithmetic
            SystemStats = new Dnd5eExtension { WarmthRating = 25f },
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 0 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        // Arctic baseline -20 + amplitude 6 * night multiplier -1.0 = -26; plus warmth 25 = -1 (comfortable, not extreme).
        Assert.Equal(-1f, delta.Value);
        Assert.True(delta.Value > -20f);
    }

    [Fact]
    public async Task ApplyAsync_FursInDesert_PushesFeltTemperatureIntoHeatExtreme()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location
        {
            Id = "locations/climate_exposure_desert_furs",
            Name = "Desert Dunes",
            ClimateZone = ClimateZone.Desert,
        };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();

        var character = new Character
        {
            Id = "chars/climate_exposure_furs_desert",
            Name = "Overdressed Traveler",
            CurrentLocationId = location.Id,
            IsPc = true, // PCs are always in exposure scope; these tests are about the arithmetic
            SystemStats = new Dnd5eExtension { WarmthRating = 25f },
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 12 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        // Desert baseline 25 + amplitude 16 * noon multiplier 1.0 = 41; plus warmth 25 = 66 (heat extreme, >= 50).
        Assert.Equal(66f, delta.Value);
        Assert.True(delta.Value >= 50f);
    }

    [Fact]
    public async Task ApplyAsync_NakedInArctic_StaysColdExtreme()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = new Location
        {
            Id = "locations/climate_exposure_arctic_naked",
            Name = "Arctic Wastes (Naked)",
            ClimateZone = ClimateZone.Arctic,
        };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();

        var character = new Character
        {
            Id = "chars/climate_exposure_naked_arctic",
            Name = "Unclothed Traveler",
            CurrentLocationId = location.Id,
            IsPc = true, // PCs are always in exposure scope; these tests are about the arithmetic
            SystemStats = new Dnd5eExtension(), // WarmthRating defaults to 0
        };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 0 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var delta = Assert.Single(result.Deltas.OfType<AttributeChange>());
        // Arctic baseline -20 + amplitude 6 * night multiplier -1.0 = -26; plus warmth 0 = -26 (cold extreme, <= -20).
        Assert.Equal(-26f, delta.Value);
        Assert.True(delta.Value <= -20f);
    }

    [Fact]
    public async Task ApplyAsync_CharacterWithoutLocation_Skipped()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = new Character { Id = "chars/climate_exposure_no_location", Name = "Nowhere", SystemStats = new Dnd5eExtension() };

        var rule = new ClimateExposureRule();
        var time = new CampaignTime { Hour = 12 };
        var ctx = new SimulationContext(time, [], [character], session, 1);

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Deltas);
    }

    /// <summary>
    /// Shared arctic location for the scoping tests below, so each one only has to describe who is
    /// standing where.
    /// </summary>
    private async Task<Location> StoreArcticAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string id)
    {
        var location = new Location { Id = id, Name = "Arctic Wastes", ClimateZone = ClimateZone.Arctic };
        await session.StoreAsync(location);
        await session.SaveChangesAsync();
        return location;
    }

    [Fact]
    public async Task ApplyAsync_NpcStandingWithTheParty_GetsAReading()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var location = await StoreArcticAsync(session, "locations/climate_scope_with_party");

        var pc = new Character
        {
            Id = "chars/climate_scope_pc", Name = "Aria", IsPc = true,
            CurrentLocationId = location.Id, SystemStats = new Dnd5eExtension()
        };
        var guide = new Character
        {
            Id = "chars/climate_scope_guide", Name = "Guide",
            CurrentLocationId = location.Id, SystemStats = new Dnd5eExtension()
        };

        var rule = new ClimateExposureRule();
        var ctx = new SimulationContext(new CampaignTime { Hour = 0 }, [], [pc, guide], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var written = result.Deltas.OfType<AttributeChange>().ToDictionary(d => d.CharacterId, d => d.Value);
        Assert.Equal(-26f, written[pc.Id]);
        // If the PCs are freezing in the pass, so is the guide standing next to them.
        Assert.Equal(-26f, written[guide.Id]);
    }

    [Fact]
    public async Task ApplyAsync_OffScreenNpc_GetsNoReading()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var partyLocation = await StoreArcticAsync(session, "locations/climate_scope_party_here");
        var elsewhere = await StoreArcticAsync(session, "locations/climate_scope_elsewhere");

        var pc = new Character
        {
            Id = "chars/climate_scope_pc_here", Name = "Aria", IsPc = true,
            CurrentLocationId = partyLocation.Id, SystemStats = new Dnd5eExtension()
        };
        // Off screen, and already sitting at the neutral default — nothing to say about them.
        var hermit = new Character
        {
            Id = "chars/climate_scope_hermit", Name = "Hermit",
            CurrentLocationId = elsewhere.Id, SystemStats = new Dnd5eExtension()
        };

        var rule = new ClimateExposureRule();
        var ctx = new SimulationContext(new CampaignTime { Hour = 0 }, [], [pc, hermit], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var written = result.Deltas.OfType<AttributeChange>().Select(d => d.CharacterId).ToList();
        Assert.Contains(pc.Id, written);
        Assert.DoesNotContain(hermit.Id, written);
    }

    /// <summary>
    /// The staleness guard. An NPC the party met in the desert must not keep radiating "suffering from
    /// extreme heat" pressure forever once they are off screen — CharacterDistressPressureContributor
    /// reads every KeepAlive character regardless of where they are, so a frozen extreme reading would
    /// be indistinguishable from a live one.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NpcLeavingScopeWithStaleReading_IsResetOnce()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var partyLocation = await StoreArcticAsync(session, "locations/climate_scope_reset_party");
        var elsewhere = await StoreArcticAsync(session, "locations/climate_scope_reset_elsewhere");

        var pc = new Character
        {
            Id = "chars/climate_scope_reset_pc", Name = "Aria", IsPc = true,
            CurrentLocationId = partyLocation.Id, SystemStats = new Dnd5eExtension()
        };
        var formerCompanion = new Character
        {
            Id = "chars/climate_scope_reset_npc", Name = "Desert Guide",
            CurrentLocationId = elsewhere.Id,
            SystemStats = new Dnd5eExtension { Temperature = 41f } // last read while travelling with the party
        };

        var rule = new ClimateExposureRule();
        var ctx = new SimulationContext(new CampaignTime { Hour = 0 }, [], [pc, formerCompanion], session, 1);

        var result = await rule.ApplyAsync(ctx);

        var reset = Assert.Single(result.Deltas.OfType<AttributeChange>(), d => d.CharacterId == formerCompanion.Id);
        Assert.Equal(20f, reset.Value);
        Assert.False(reset.IsDelta);

        // Converges: once the reading is neutral, later ticks leave them alone entirely.
        formerCompanion.SystemStats!.Temperature = 20f;
        var second = await rule.ApplyAsync(ctx);
        Assert.DoesNotContain(second.Deltas.OfType<AttributeChange>(), d => d.CharacterId == formerCompanion.Id);
    }
}
