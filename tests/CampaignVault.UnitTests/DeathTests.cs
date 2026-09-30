using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Tools;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class DeathTests
{
    private readonly Character _npc = new()
    {
        Id = "chars/skiv", Name = "Skiv", MaxHp = 10, CurrentHp = 4, KeepAlive = true, IsPartyCompanion = true,
        CurrentLocationId = "locations/gorse", CurrentActivity = "limping", Schedule = new Schedule(),
    };

    private readonly Character _pc = new()
    {
        Id = "chars/maeve", Name = "Maeve", MaxHp = 20, CurrentHp = 5, IsPc = true, KeepAlive = true,
        CurrentLocationId = "locations/gorse",
    };

    private ChangeContext Ctx() => ChangeContextTestHelper.Create(
        characters: new() { [_npc.Id] = _npc, [_pc.Id] = _pc },
        summary: []);

    private static Task<ChangeHandlerResult> Run(DeathChange c, ChangeContext ctx) =>
        new DeathChangeHandler().ApplyAsync(c, ctx, TestContext.Current.CancellationToken);

    [Fact]
    public void DeathChange_RoundTripsThroughPolymorphicJson()
    {
        const string json = """{"$type":"death","characterId":"chars/skiv","cause":"goblin arrow","killerId":"chars/g1"}""";
        var change = JsonSerializer.Deserialize<WorldChange>(json);

        var death = Assert.IsType<DeathChange>(change);
        Assert.Equal("chars/skiv", death.CharacterId);
        Assert.Equal("goblin arrow", death.Cause);
        Assert.Equal("chars/g1", death.KillerId);
        Assert.False(death.Revive);
    }

    [Fact]
    public void Character_IsDead_IsNotPersisted_ButDeathIs()
    {
        var c = new Character { Id = "chars/x", Name = "X", Death = new DeathRecord { Day = 3, Cause = "fall" } };
        var json = JsonSerializer.Serialize(c);

        Assert.DoesNotContain("IsDead", json);
        var back = JsonSerializer.Deserialize<Character>(json)!;
        Assert.True(back.IsDead);
        Assert.Equal("fall", back.Death!.Cause);
        Assert.False(new Character { Id = "chars/y", Name = "Y" }.IsDead);
    }

    [Fact]
    public async Task Death_Npc_RecordsBody_LeavesScene_AndPublishesEvent()
    {
        var ctx = Ctx();
        var result = await Run(new DeathChange { CharacterId = _npc.Id, Cause = "goblin arrow", KillerId = "chars/g1" }, ctx);

        Assert.True(result.Success);
        Assert.True(_npc.IsDead);
        Assert.Equal("goblin arrow", _npc.Death!.Cause);
        Assert.Equal("chars/g1", _npc.Death.KillerId);
        Assert.Equal("locations/gorse", _npc.Death.BodyLocationId);
        Assert.Equal(0, _npc.CurrentHp);
        Assert.Null(_npc.CurrentLocationId);
        Assert.Null(_npc.CurrentActivity);
        Assert.Null(_npc.Schedule);
        Assert.False(_npc.KeepAlive);
        Assert.False(_npc.IsPartyCompanion);

        var died = Assert.Single(ctx.TakePendingEvents(), e => e.Topic == CoreEvents.CharacterDied);
        Assert.True(died.TryGet<string>(CoreEvents.Fields.BodyLocationId, out var body));
        Assert.Equal("locations/gorse", body);
    }

    [Fact]
    public async Task Death_Pc_KeepsPartyLocationAndFlags()
    {
        var result = await Run(new DeathChange { CharacterId = _pc.Id, Cause = "starved" }, Ctx());

        Assert.True(result.Success);
        Assert.True(_pc.IsDead);
        Assert.True(_pc.IsPc);
        Assert.True(_pc.KeepAlive);
        Assert.Equal("locations/gorse", _pc.CurrentLocationId);
    }

    [Fact]
    public async Task Death_Twice_IsANoOp_AndPublishesOnce()
    {
        var ctx = Ctx();
        await Run(new DeathChange { CharacterId = _npc.Id, Cause = "first" }, ctx);
        var again = await Run(new DeathChange { CharacterId = _npc.Id, Cause = "second" }, ctx);

        Assert.True(again.Success);
        Assert.Equal("first", _npc.Death!.Cause);
        Assert.Single(ctx.TakePendingEvents(), e => e.Topic == CoreEvents.CharacterDied);
    }

    [Fact]
    public async Task Death_UnknownCharacter_Fails()
    {
        var result = await Run(new DeathChange { CharacterId = "chars/nobody" }, Ctx());
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Revive_RestoresHpAndBodyLocation()
    {
        var ctx = Ctx();
        await Run(new DeathChange { CharacterId = _npc.Id, Cause = "arrow" }, ctx);
        var result = await Run(new DeathChange { CharacterId = _npc.Id, Revive = true, Hp = 3 }, ctx);

        Assert.True(result.Success);
        Assert.False(_npc.IsDead);
        Assert.Equal(3, _npc.CurrentHp);
        Assert.Equal("locations/gorse", _npc.CurrentLocationId);
    }

    [Fact]
    public async Task Revive_CapsHpAtMax_AndHonoursNewLocation()
    {
        var ctx = Ctx();
        await Run(new DeathChange { CharacterId = _npc.Id }, ctx);
        await Run(new DeathChange { CharacterId = _npc.Id, Revive = true, Hp = 99, NewLocationId = "locations/camp" }, ctx);

        Assert.Equal(10, _npc.CurrentHp);
        Assert.Equal("locations/camp", _npc.CurrentLocationId);
    }

    [Fact]
    public async Task Revive_LivingCharacter_Fails()
    {
        var result = await Run(new DeathChange { CharacterId = _npc.Id, Revive = true }, Ctx());
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Hp_Heal_OnDeadCharacter_IsRejected_WithPointerToRevive()
    {
        var ctx = Ctx();
        await Run(new DeathChange { CharacterId = _npc.Id }, ctx);

        var heal = await new HpChangeHandler(Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = _npc.Id, Delta = 5 }, ctx, TestContext.Current.CancellationToken);

        Assert.False(heal.Success);
        Assert.Equal(0, _npc.CurrentHp);
        Assert.Contains("revive", heal.Message);
    }

    [Fact]
    public async Task Dead_AreNotCombatants()
    {
        await Run(new DeathChange { CharacterId = _npc.Id }, Ctx());
        _npc.CurrentHp = 5; // even with HP written back some other way
        _npc.CampaignName = "camp";

        Assert.False(CampaignEntityVisibility.IsCombatantAllowed(_npc, "camp"));
    }

    [Fact]
    public void ArchiveEntity_CharacterValue_PointsAtDeathVerb()
    {
        var ex = new JsonException(
            "The JSON value could not be converted to System.Nullable`1[CampaignVault.Models.ArchivableEntityType]. Path: $.changes[0].entityType",
            path: "$.changes[0].entityType", lineNumber: 0, bytePositionInLine: 0);
        using var doc = JsonDocument.Parse("""{"changes":[{"$type":"archive_entity","entityId":"chars/skiv","entityType":"Character"}]}""");

        var enriched = ModelEnumErrorHints.Enrich(ex, doc.RootElement);

        Assert.Contains("\"$type\": \"death\"", enriched);
    }

    [Fact]
    public async Task ArchiveEntity_MissingEntityType_PointsAtDeathVerb()
    {
        var result = await new ArchiveEntityChangeHandler().ApplyAsync(
            new ArchiveEntityChange { EntityId = "chars/skiv" }, Ctx(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("death", result.Message);
    }
}
