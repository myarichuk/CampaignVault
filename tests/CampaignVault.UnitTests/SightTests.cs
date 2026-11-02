using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.Witness;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>IMPRESSIONS_PLAN.md I1: distance and light decide who sees a visible beat; hearing is untouched.</summary>
public class SightResolverTests
{
    private static Location Place(LocationType type, LightLevel? lighting = null) =>
        new() { Id = "locations/x", Name = "X", Description = "", Type = type, Lighting = lighting };

    private static CampaignTime At(int hour) => new() { Hour = hour };

    [Theory]
    [InlineData(LocationType.Wilderness, 12, LightLevel.Bright)]
    [InlineData(LocationType.Wilderness, 18, LightLevel.Dim)]
    [InlineData(LocationType.Wilderness, 6, LightLevel.Dim)]
    [InlineData(LocationType.Wilderness, 22, LightLevel.Dark)]
    [InlineData(LocationType.Wilderness, 3, LightLevel.Dark)]
    [InlineData(LocationType.Settlement, 23, LightLevel.Dim)]
    [InlineData(LocationType.Room, 12, LightLevel.Dim)]
    [InlineData(LocationType.Building, 23, LightLevel.Dim)]
    public void The_light_follows_the_place_and_the_hour(LocationType type, int hour, LightLevel expected) =>
        Assert.Equal(expected, SightResolver.LightAt(Place(type), At(hour)));

    [Fact]
    public void Lighting_set_on_the_location_wins_and_nothing_known_hides_nothing()
    {
        Assert.Equal(LightLevel.Dark, SightResolver.LightAt(Place(LocationType.Room, LightLevel.Dark), At(12)));
        Assert.Equal(LightLevel.Bright, SightResolver.LightAt(Place(LocationType.Wilderness, LightLevel.Bright), At(23)));
        Assert.Equal(LightLevel.Bright, SightResolver.LightAt(null, At(23)));
        Assert.Equal(LightLevel.Bright, SightResolver.LightAt(Place(LocationType.Wilderness), null));
    }

    [Fact]
    public void Darkvision_comes_from_the_attribute_or_the_race_trait()
    {
        Assert.Equal(0, SightResolver.DarkvisionFeet(new Character { Id = "chars/a", Name = "A" }));
        Assert.Equal(60, SightResolver.DarkvisionFeet(new Character { Id = "chars/a", Name = "A", DistinctiveFeatures = ["Size: Small", "Darkvision 60 ft."] }));
        Assert.Equal(SightResolver.UnlimitedDarkvision, SightResolver.DarkvisionFeet(new Character { Id = "chars/a", Name = "A", DistinctiveFeatures = ["Darkvision"] }));
        var stats = new Dnd5eExtension();
        stats.Attributes["darkvision"] = 120;
        Assert.Equal(120, SightResolver.DarkvisionFeet(new Character { Id = "chars/a", Name = "A", SystemStats = stats, DistinctiveFeatures = ["Darkvision 60 ft."] }));
    }

    private static readonly Character Maeve = new() { Id = "chars/maeve", Name = "Maeve" };

    private static Character Observer(string? band = null, int darkvision = 0, bool departing = false)
    {
        var stats = new Dnd5eExtension();
        if (band != null)
        {
            stats.SpatialPositions.Add(new SpatialPosition { TargetId = Maeve.Id, DistanceBand = band });
        }

        if (darkvision > 0)
        {
            stats.Attributes["darkvision"] = darkvision;
        }

        return new Character { Id = "chars/o", Name = "O", SystemStats = stats, Departing = departing ? new Departing() : null };
    }

    [Theory]
    [InlineData(LightLevel.Dark, "Close", 0, true)]
    [InlineData(LightLevel.Dark, null, 0, false)] // no band recorded is Near: 30 ft, past Close
    [InlineData(LightLevel.Dark, "Near", 60, true)]
    [InlineData(LightLevel.Dark, "Far", 60, false)]
    [InlineData(LightLevel.Dim, "Far", 0, true)]
    [InlineData(LightLevel.Dim, "Distant", 0, false)]
    [InlineData(LightLevel.Bright, "Distant", 0, true)]
    public void Eyes_reach_as_far_as_the_light_and_darkvision_allow(LightLevel light, string? band, int darkvision, bool sees) =>
        Assert.Equal(sees, SightResolver.Sees(Observer(band, darkvision), new SightContext(light, false, Maeve)).Sees);

    [Fact]
    public void A_band_recorded_on_the_source_counts_too()
    {
        var stats = new Dnd5eExtension();
        stats.SpatialPositions.Add(new SpatialPosition { TargetId = "chars/o", DistanceBand = "Far" });
        var source = new Character { Id = Maeve.Id, Name = "Maeve", SystemStats = stats };
        Assert.Equal("Far", SightResolver.BandBetween(Observer(), source));
    }

    [Fact]
    public void Something_that_makes_its_own_light_is_seen_in_the_dark_at_any_distance() =>
        Assert.True(SightResolver.Sees(Observer("Distant"), new SightContext(LightLevel.Dark, true, Maeve)).Sees);

    [Fact]
    public void Someone_leaving_is_already_distant()
    {
        Assert.Equal(SpatialDistanceBand.Distant, SightResolver.BandBetween(Observer("Close", departing: true), Maeve));
        var line = SightResolver.Sees(Observer("Close", departing: true), new SightContext(LightLevel.Dim, false, Maeve));
        Assert.False(line.Sees);
        Assert.Equal("distant, too far to make it out in the dim light", line.Note);
    }

    [Fact]
    public void A_blocked_eye_still_hears_a_loud_beat()
    {
        var o = Observer("Far");
        var notice = WitnessSelector.Perceive(o, Data.Context.Perceptible.Both, Maeve, new SightContext(LightLevel.Dark, false, Maeve));
        Assert.Equal(Data.Context.Noticed.Perceives, notice.Outcome);
        Assert.Equal(Data.Context.Perceptible.Loud, notice.Senses);
        Assert.Equal("far, too dark to see it; heard it only", notice.Note);

        var silent = WitnessSelector.Perceive(o, Data.Context.Perceptible.Visible, Maeve, new SightContext(LightLevel.Dark, false, Maeve));
        Assert.False(silent.Perceived);
    }
}

/// <summary>I1 through take_turn: the night road, a trader far up it, another by the fire.</summary>
[Collection("RavenDB")]
public class NightRoadSightTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public NightRoadSightTests(RavenDBFixture fixture) => _fixture = fixture;

    internal sealed record Road(string Slug, CampaignTools Tools, string LocId, string PcId, string GoblinId, string CorrenId, string IlsaId);

    /// <summary>Corren is Far up the road from Maeve; Ilsa stands Close by her. The road is dark.</summary>
    internal static async Task<Road> SeedRoadAsync(RavenDBFixture fixture, string lighting = "dark", bool startCombat = true)
    {
        var slug = "sight-" + Guid.NewGuid().ToString("N")[..8];
        var repo = fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-road";
        var pcId = $"chars/{slug}-maeve";
        var goblinId = $"chars/{slug}-goblin";
        var correnId = $"chars/{slug}-corren";
        var ilsaId = $"chars/{slug}-ilsa";
        using (var session = fixture.Store.OpenAsyncSession())
        {
            var cs = fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId, Name = "North Road", Description = "A rutted road through dry grass.", Type = LocationType.Wilderness
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = pcId, Name = "Maeve", IsPc = true, CurrentLocationId = locId, MaxHp = 18, CurrentHp = 18,
                SystemStats = new Dnd5eExtension { ArmorClass = 12 }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = goblinId, Name = "Goblin Scout", CurrentLocationId = locId, MaxHp = 7, CurrentHp = 7,
                SystemStats = new Dnd5eExtension { ArmorClass = 15, Dexterity = 14 }
            });
            var far = new Dnd5eExtension();
            far.SpatialPositions.Add(new SpatialPosition { TargetId = pcId, DistanceBand = SpatialDistanceBand.Far });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = correnId, Name = "Corren", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9, SystemStats = far
            });
            var close = new Dnd5eExtension();
            close.SpatialPositions.Add(new SpatialPosition { TargetId = pcId, DistanceBand = SpatialDistanceBand.Close });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = ilsaId, Name = "Ilsa", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9, SystemStats = close
            });
            await session.SaveChangesAsync();
        }

        var lit = await tools.TakeTurn(new TakeTurnRequest
        {
            FullDetailLocationId = locId,
            Changes = [new LocationUpdate { LocationId = locId, Lighting = lighting }],
            Narrative = "Night settles over the road."
        }, slug);
        Assert.True(lit.Success, lit.Summary);

        var road = new Road(slug, tools, locId, pcId, goblinId, correnId, ilsaId);
        if (startCombat)
        {
            var started = await tools.StartCombat(locId, [pcId, goblinId], slug);
            Assert.True(started.Success, started.Summary);
            await ToMaevesTurnAsync(road, started.Data!.ActiveTurnId);
        }

        return road;
    }

    internal static async Task ToMaevesTurnAsync(Road r, string? active)
    {
        for (var i = 0; i < 4 && !string.Equals(active, r.PcId, StringComparison.OrdinalIgnoreCase); i++)
        {
            var next = await r.Tools.NextTurn(campaignName: r.Slug);
            Assert.True(next.Success, next.Summary);
            active = next.Data!.ActiveTurnId;
        }
    }

    private static Task<ToolResult<TurnResult>> CastAsync(Road r, string spell, Dictionary<string, string> parameters, List<string>? targets = null) =>
        r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = r.PcId, TargetIds = targets ?? [], ActionType = RulesetActionType.Spell,
                    ActionCategory = ActionCategory.Spell, ActionName = spell, Parameters = parameters
                }
            ],
            Narrative = $"Maeve casts {spell}."
        }, r.Slug);

    [Fact]
    public async Task An_image_in_the_dark_is_seen_by_the_one_close_by_not_the_one_far_up_the_road()
    {
        var r = await SeedRoadAsync(_fixture);

        var cast = await CastAsync(r, "minor_illusion",
            new() { ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "image" });

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.Contains("Ilsa", line);
        Assert.DoesNotContain("Corren", line);
    }

    [Fact]
    public async Task A_sound_in_the_dark_still_carries_up_the_road()
    {
        var r = await SeedRoadAsync(_fixture);

        var cast = await CastAsync(r, "minor_illusion",
            new() { ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "sound" });

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.Contains("Corren", line);
        Assert.Contains("Ilsa", line);
    }

    [Fact]
    public async Task A_fireball_lights_itself_so_the_far_trader_sees_it()
    {
        var r = await SeedRoadAsync(_fixture);

        var cast = await CastAsync(r, "fireball",
            new() { ["resolution"] = "save", ["save"] = "Dexterity", ["damageDice"] = "8d6", ["innate"] = "true" }, [r.GoblinId]);

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.Contains("Corren", line);
        Assert.DoesNotContain("Corren (", line);
    }

    [Fact]
    public async Task By_daylight_the_far_trader_sees_the_image_too()
    {
        var r = await SeedRoadAsync(_fixture, lighting: "bright");

        var cast = await CastAsync(r, "minor_illusion",
            new() { ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "image" });

        Assert.True(cast.Success, cast.Summary);
        Assert.Contains("Corren", Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
    }

    [Fact]
    public async Task Lighting_that_isnt_a_light_level_fails_the_commit()
    {
        var r = await SeedRoadAsync(_fixture, startCombat: false);

        var bad = await r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new LocationUpdate { LocationId = r.LocId, Lighting = "gloomy" }],
            Narrative = "The road turns gloomy."
        }, r.Slug);

        Assert.False(bad.Success);
        Assert.Contains("lighting 'gloomy'", string.Join("\n", bad.Data?.Summary ?? [bad.Summary ?? ""]) + bad.Summary);
    }
}
