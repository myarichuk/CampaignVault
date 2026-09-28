using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Middleware;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Dirt against a real database through the real tools: it persists, it rides the wire as one short line in scene and
/// party shapes, the full marks only come back from get_entity, and take_turn's delta mode resends it only when a
/// soil touched it.
/// </summary>
[Collection("RavenDB")]
public class SoilEndToEndTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public SoilEndToEndTests(RavenDBFixture fixture) => _fixture = fixture;

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static bool WireHasKey<T>(T value, string key)
    {
        var element = JsonSerializer.SerializeToElement(value, Wire);
        var cleaned = McpResponseCleaner.Clean(element);
        var json = cleaned == null ? element : JsonSerializer.SerializeToElement(cleaned);
        return json.EnumerateObject().Any(p => p.Name == key);
    }

    [Fact]
    public async Task Dirt_Persists_RidesTheWireCompactly_AndFullDetailComesFromGetEntity()
    {
        var slug = "soil-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        var deepDive = TestCampaignToolsFactory.CreateDeepDiveTools(_fixture, repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var hall = $"locations/{slug}-hall";
        var pc = $"chars/{slug}-pc";
        var guard = $"chars/{slug}-guard";
        var boots = $"items/{slug}-boots";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = hall, Name = "Hall" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pc, Name = "Ana", IsPc = true, CurrentLocationId = hall, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = guard, Name = "Guard", CurrentLocationId = hall, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = boots, Name = "Boots", HolderId = guard });
            (await session.LoadAsync<Item>(boots, TestContext.Current.CancellationToken))!.IsEquipped = true;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Task<ToolResult<TurnResult>> Turn(WorldChange[]? changes = null, bool full = false) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "The brawl ends." : null,
            PartyLocationId = hall,
            ExtraLocationIds = [hall],
            ExtraCharacterIds = [guard],
            IncludeParty = full,
            ForceFullReseed = full
        }, slug);

        var seed = await Turn(full: true);
        Assert.True(seed.Success, seed.Summary);

        // ---- the fight ends; the DM commits what it left behind, in one batch --------------------------------------
        var brawl = await Turn(
        [
            new SoilChange { TargetId = pc, Kind = "blood", Spot = "hands", Amount = 2 },
            new SoilChange { TargetId = guard, Kind = "mud", Spot = "boots" },
            new SoilChange { TargetId = boots, Kind = "mud" },
            new SoilChange { TargetId = hall, Kind = "scorch", Fixture = "north wall", Amount = 2 },
            new SoilChange { TargetId = hall, Kind = "debris", Fixture = "floor" },
        ]);
        Assert.True(brawl.Success, brawl.Summary);
        Assert.Equal(TurnMode.Delta, brawl.Data!.Mode);

        // Delta mode resends only what a soil touched: the scarred hall, the muddy guard, and the guard's boots.
        Assert.True(brawl.Data.Scenes?.Any(s => s.Location.Id == hall) == true,
            "scenes: " + string.Join(",", (brawl.Data.Scenes ?? []).Select(s => s.Location.Id)));
        var hallScene = brawl.Data.Scenes!.Single(s => s.Location.Id == hall);
        var scene = hallScene.Location;
        Assert.Equal("scorched north wall, slightly littered floor", scene.Soil);
        Assert.Null(scene.Dirt);
        // (a scene-present NPC rides in the scene's PresentNPCs, not in Npcs[])
        var guardRow = hallScene.PresentNPCs.Single(n => n.Id == guard);
        Assert.Equal("slightly muddy boots", guardRow.Soil);

        // ---- durable ---------------------------------------------------------------------------------------------
        using (var check = _fixture.Store.OpenAsyncSession())
        {
            var savedPc = await check.LoadAsync<Character>(pc, TestContext.Current.CancellationToken);
            var mark = Assert.Single(savedPc!.Dirt);
            Assert.Equal(("blood", 2, "hands"), (mark.Kind, mark.Severity, mark.Spot));
            Assert.Equal(2, (await check.LoadAsync<Location>(hall, TestContext.Current.CancellationToken))!.Dirt.Count);
            Assert.Single((await check.LoadAsync<Item>(boots, TestContext.Current.CancellationToken))!.Dirt);
        }

        // ---- the guard's card is out, so a later soil on the boots rides the row (holder resolved from the item) ---------
        var worse = await Turn([new SoilChange { TargetId = boots, Kind = "mud" }]);
        Assert.True(worse.Success, worse.Summary);
        var worseRow = worse.Data!.Scenes!.Single(s => s.Location.Id == hall).PresentNPCs.Single(n => n.Id == guard);
        Assert.Equal("muddy", Assert.Single(worseRow.EquippedItems!, i => i.Id == boots).Soil);

        // ---- a quiet delta turn resends none of it ---------------------------------------------------------------
        var quiet = await Turn();
        Assert.Equal(TurnMode.Delta, quiet.Data!.Mode);
        var quietScene = quiet.Data.Scenes!.Single(s => s.Location.Id == hall).Location;
        Assert.Null(quietScene.Soil);
        Assert.Null(quietScene.Dirt);
        Assert.DoesNotContain(quiet.Data.Scenes!.Single(s => s.Location.Id == hall).PresentNPCs, n => n.Soil != null);

        // ---- a Full party payload carries the compact line, never the list -------------------------------------
        var reseed = await Turn(full: true);
        var member = reseed.Data!.Party!.Single(p => p.Id == pc);
        Assert.Equal("bloody hands", member.Character.Soil);
        Assert.Null(member.Character.Dirt);
        Assert.Equal("slightly muddy boots",
            reseed.Data.Scenes!.Single(s => s.Location.Id == hall).PresentNPCs.Single(n => n.Id == guard).Soil);

        // ---- get_entity is the full-detail fetch ----------------------------------------------------------------
        var pcEntity = (await deepDive.GetEntity(pc, slug)).Data as NpcContextView;
        Assert.Null(pcEntity!.Character.Soil);
        Assert.Equal("blood", Assert.Single(pcEntity.Character.Dirt!).Kind);

        var hallEntity = (await deepDive.GetEntity(hall, slug)).Data as SceneView;
        Assert.Null(hallEntity!.Location.Soil);
        Assert.Equal(2, hallEntity.Location.Dirt!.Count);

        var bootsEntity = (await deepDive.GetEntity(boots, slug)).Data as Item;
        Assert.Equal("mud", Assert.Single(bootsEntity!.Dirt).Kind);

        // A plain scene refresh stays on the one-line summary.
        var refreshed = await tools.GetScene(hall, campaignName: slug);
        Assert.NotNull(refreshed.Data!.Location.Soil);
        Assert.Null(refreshed.Data.Location.Dirt);

        // ---- wash and clear after a rest -------------------------------------------------------------------------
        var wash = await Turn(
        [
            new SoilChange { TargetId = pc, Kind = "blood", Amount = -5 },
            new SoilChange { TargetId = hall, Clear = true },
        ]);
        Assert.True(wash.Success, wash.Summary);

        var cleanPc = (await deepDive.GetEntity(pc, slug)).Data as NpcContextView;
        Assert.Null(cleanPc!.Character.Dirt);
        Assert.Null(cleanPc.Character.Soil);
        Assert.False(WireHasKey(cleanPc.Character, "soil"));
        Assert.False(WireHasKey(cleanPc.Character, "dirt"));
        Assert.False(WireHasKey(ItemSummaryView.From(new Item { Id = "items/x", Name = "X" }), "soil"));
        var cleanHall = (await deepDive.GetEntity(hall, slug)).Data as SceneView;
        Assert.Null(cleanHall!.Location.Dirt);
    }
}
