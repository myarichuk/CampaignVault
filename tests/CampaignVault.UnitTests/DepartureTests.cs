using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>IMPRESSIONS_PLAN.md I2: someone walking away is distant before they're gone, and an encounter stub is named once.</summary>
[Collection("RavenDB")]
public class DepartureTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public DepartureTests(RavenDBFixture fixture) => _fixture = fixture;

    private static Task<ToolResult<TurnResult>> CommitAsync(NightRoadSightTests.Road r, string narrative, params WorldChange[] changes) =>
        r.Tools.TakeTurn(new TakeTurnRequest { Changes = [.. changes], Narrative = narrative }, r.Slug);

    private static Task<ToolResult<TurnResult>> BeatAsync(NightRoadSightTests.Road r) =>
        CommitAsync(r, "Maeve watches the road.",
            new EventOccurred { Summary = "Maeve watches the road", Category = EventCategory.Discovery, Involved = [r.PcId], LocationId = r.LocId });

    private static Task<ToolResult<TurnResult>> IllusionAsync(NightRoadSightTests.Road r, string mode) =>
        CommitAsync(r, "Maeve weaves a small illusion.", new RulesetAction
        {
            CharacterId = r.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "minor_illusion",
            Parameters = new Dictionary<string, string> { ["resolution"] = "utility", ["innate"] = "true", ["mode"] = mode }
        });

    private static ActivityChange Leave(string id, string? to = null) =>
        new() { CharacterId = id, NewActivity = "walking off with the caravan", Departing = true, NewLocationId = to };

    private async Task<Character> LoadAsync(string id)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        return (await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Someone_leaving_in_dim_light_doesnt_see_a_small_image_but_hears_a_sound()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, lighting: "dim", startCombat: false);
        var left = await CommitAsync(r, "Ilsa waves and walks off after the caravan.", Leave(r.IlsaId));
        Assert.True(left.Success, left.Summary);
        Assert.Contains(left.Data!.Summary ?? [], l => l.StartsWith("Ilsa is leaving"));

        var image = await IllusionAsync(r, "image");
        Assert.True(image.Success, image.Summary);
        Assert.DoesNotContain(image.Data!.Context ?? [], l => l.StartsWith("Witnesses") && l.Contains("Ilsa"));

        var r2 = await NightRoadSightTests.SeedRoadAsync(_fixture, lighting: "dim", startCombat: false);
        Assert.True((await CommitAsync(r2, "Ilsa walks off.", Leave(r2.IlsaId))).Success);
        var sound = await IllusionAsync(r2, "sound");
        Assert.Contains("Ilsa", Assert.Single(sound.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
    }

    [Fact]
    public async Task A_departure_completes_after_two_more_beats()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        var fork = $"locations/{r.Slug}-fork";
        Assert.True((await CommitAsync(r, "The road forks ahead.",
            new LocationUpdate { LocationId = fork, Name = "The Fork", Type = LocationType.Wilderness })).Success);

        Assert.True((await CommitAsync(r, "Corren heads for the fork.", Leave(r.CorrenId, fork))).Success);
        Assert.NotNull((await LoadAsync(r.CorrenId)).Departing);

        var first = await BeatAsync(r);
        Assert.True(first.Success, first.Summary);
        Assert.Equal(r.LocId, (await LoadAsync(r.CorrenId)).CurrentLocationId, ignoreCase: true);

        var second = await BeatAsync(r);
        Assert.True(second.Success, second.Summary);
        Assert.Contains(second.Data!.Summary ?? [], l => l == "Corren has gone on to The Fork and is no longer here.");
        var corren = await LoadAsync(r.CorrenId);
        Assert.Equal(fork, corren.CurrentLocationId, ignoreCase: true);
        Assert.Null(corren.Departing);
    }

    [Fact]
    public async Task With_no_destination_they_go_off_scene()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        Assert.True((await CommitAsync(r, "Corren walks off.", Leave(r.CorrenId))).Success);
        await BeatAsync(r);
        var second = await BeatAsync(r);

        Assert.Contains(second.Data!.Summary ?? [], l => l == "Corren is out of sight and no longer here.");
        var corren = await LoadAsync(r.CorrenId);
        Assert.Null(corren.CurrentLocationId);
        Assert.Equal(r.LocId, corren.DepartedFromLocationId, ignoreCase: true);
    }

    [Fact]
    public async Task Turning_back_cancels_and_departing_with_updateLocation_is_refused()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        Assert.True((await CommitAsync(r, "Corren walks off.", Leave(r.CorrenId))).Success);
        var back = await CommitAsync(r, "Corren turns back.", new ActivityChange { CharacterId = r.CorrenId, Departing = false });
        Assert.True(back.Success, back.Summary);
        Assert.Null((await LoadAsync(r.CorrenId)).Departing);

        var both = await CommitAsync(r, "Corren leaves.",
            new ActivityChange { CharacterId = r.CorrenId, Departing = true, UpdateLocation = true, NewLocationId = r.LocId });
        Assert.False(both.Success);
    }

    [Fact]
    public async Task Encounter_stubs_are_counted_once_not_named_as_witnesses()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, lighting: "bright", startCombat: false);
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            foreach (var n in new[] { "a", "b" })
            {
                await session.StoreAsync(new Character
                {
                    Id = $"chars/transient_encounter_{r.Slug}{n}", Name = Data.EncounterResolver.UnnamedEntityName, CampaignName = r.Slug,
                    CurrentLocationId = r.LocId, MaxHp = 5, CurrentHp = 5
                }, TestContext.Current.CancellationToken);
            }

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var sound = await IllusionAsync(r, "sound");
        var line = Assert.Single(sound.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.DoesNotContain(Data.EncounterResolver.UnnamedEntityName, line);
        Assert.Contains("2 unnamed encounter stubs [", line);
    }
}
