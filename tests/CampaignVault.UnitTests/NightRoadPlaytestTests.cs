using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// IMPRESSIONS_PLAN.md I7: the playtest that started it, on a scratch campaign. The night road; the caravan (Lys, Sera, Corren far
/// up the road) heading off its own way; Brenna, a child, close by Maeve. Butterflies for Brenna, then a ghost for the lone goblin
/// scout, four times. Each line is written to the test output.
/// </summary>
[Collection("RavenDB")]
public class NightRoadPlaytestTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public NightRoadPlaytestTests(RavenDBFixture fixture) => _fixture = fixture;

    private static void Log(string beat, ToolResult<TurnResult> result)
    {
        foreach (var line in (result.Data?.Summary ?? []).Where(l => l.StartsWith("IMPRESSIONS") || l.StartsWith("ILLUSION") || l.Contains("is leaving"))
                 .Concat((result.Data?.Context ?? []).Where(l => l.StartsWith("Witnesses") || l.StartsWith("Heard from"))))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"[{beat}] {line}");
        }
    }

    [Fact]
    public async Task The_night_road_playtest()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        var lys = $"chars/{r.Slug}-lys";
        var sera = $"chars/{r.Slug}-sera";
        var brenna = $"chars/{r.Slug}-brenna";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            foreach (var (id, name) in new[] { (lys, "Lys"), (sera, "Sera") })
            {
                await session.StoreAsync(new Character
                {
                    Id = id, Name = name, CampaignName = r.Slug, CurrentLocationId = r.LocId, MaxHp = 9, CurrentHp = 9, LifeStage = LifeStage.Adult
                }, TestContext.Current.CancellationToken);
            }

            var close = new Dnd5eExtension();
            close.SpatialPositions.Add(new SpatialPosition { TargetId = r.PcId, DistanceBand = SpatialDistanceBand.Close });
            await session.StoreAsync(new Character
            {
                Id = brenna, Name = "Brenna", CampaignName = r.Slug, CurrentLocationId = r.LocId, MaxHp = 4, CurrentHp = 4,
                LifeStage = LifeStage.Child, SystemStats = close
            }, TestContext.Current.CancellationToken);

            var goblin = (await session.LoadAsync<Character>(r.GoblinId, TestContext.Current.CancellationToken))!;
            goblin.DistinctiveFeatures = ["Darkvision 60 ft."];
            goblin.SystemStats.Morale = 30;
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The caravan pulls away: Lys and Sera with it (Corren is already Far up the road).
        var parting = await r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new ActivityChange { CharacterId = lys, NewActivity = "walking north with the caravan", Departing = true },
                new ActivityChange { CharacterId = sera, NewActivity = "walking north with the caravan", Departing = true },
            ],
            Narrative = "The caravan creaks off north; Lys and Sera wave from the tailboard."
        }, r.Slug);
        Assert.True(parting.Success, parting.Summary);
        Log("parting", parting);

        var butterflies = await r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = r.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "minor_illusion",
                    TargetIds = [brenna],
                    Parameters = new Dictionary<string, string>
                    {
                        ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "image", ["appeal"] = "delight", ["subject"] = "a glowing butterfly"
                    }
                }
            ],
            Narrative = "Maeve cups her hands and a pale butterfly sits in them for Brenna."
        }, r.Slug);
        Assert.True(butterflies.Success, butterflies.Summary);
        Log("butterflies", butterflies);
        var delight = Assert.Single(butterflies.Data!.Summary ?? [], l => l.StartsWith("IMPRESSIONS"));
        Assert.Contains("Brenna:", delight);
        Assert.Contains("a child", delight);
        foreach (var gone in new[] { "Lys", "Sera", "Corren" })
        {
            Assert.DoesNotContain(gone + ":", delight);
        }

        var frights = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var ghost = await r.Tools.TakeTurn(new TakeTurnRequest
            {
                Changes =
                [
                    new RulesetAction
                    {
                        CharacterId = r.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "minor_illusion",
                        TargetIds = [r.GoblinId],
                        Parameters = new Dictionary<string, string>
                        {
                            ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "image", ["appeal"] = "fear", ["intensity"] = "strong",
                            ["deceptive"] = "true", ["subject"] = "a pale, grinning goblin shape", ["dc"] = "13"
                        }
                    }
                ],
                Narrative = "A pale goblin shape stands in the grass, grinning at the scout."
            }, r.Slug);
            Assert.True(ghost.Success, ghost.Summary);
            Log($"ghost {i + 1}", ghost);
            frights.Add(Assert.Single(ghost.Data!.Summary ?? [], l => l.StartsWith("IMPRESSIONS")));
            Assert.Contains(ghost.Data!.Summary ?? [], l => l.Contains("ILLUSION LIMITS (minor_illusion"));
        }

        var scoutFirst = frights[0].Split(" | ").Single(e => e.Contains("Goblin Scout:"));
        Assert.Contains("morale low", scoutFirst);
        Assert.EndsWith("→ flees", scoutFirst);
        Assert.Contains("seen it 3 times", frights[3]);
        Assert.DoesNotContain("Lys:", string.Join(" ", frights));

        // By the fourth fright the departure has completed: the caravan is off the road.
        using var check = _fixture.Store.OpenAsyncSession();
        var lysNow = await check.LoadAsync<Character>(lys, TestContext.Current.CancellationToken);
        Assert.Null(lysNow!.CurrentLocationId);
    }
}
