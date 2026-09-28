using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class TetherTests
{
    private readonly Character _horse = new() { Id = "chars/horse", Name = "Biscuit", SystemStats = new SystemExtension() };
    private readonly Character _rider = new() { Id = "chars/rider", Name = "Ana", IsPc = true, SystemStats = new SystemExtension() };
    private readonly Item _rope = new() { Id = "items/rope", Name = "Rope", HolderId = "chars/rider" };
    private readonly List<string> _summary = [];

    private ChangeContext Ctx(params WorldChange[] batch)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(
            null,
            new() { [_horse.Id] = _horse, [_rider.Id] = _rider },
            new() { [_rope.Id] = _rope },
            [], [], [], NullLogger.Instance, _summary, dispatcher);
        ctx.Batch = batch;
        return ctx;
    }

    private static async Task<ChangeHandlerResult> Run(TetherChange c, ChangeContext ctx) =>
        await new TetherChangeHandler().ApplyAsync(c, ctx, TestContext.Current.CancellationToken);

    private static TetherChange Attach(string anchor, int? dc = null, string? holder = null) => new()
    {
        Action = "attach", SubjectId = "chars/horse", AnchorId = anchor, BreakDc = dc, HolderId = holder, Label = "lead"
    };

    [Fact]
    public async Task Attach_Fixture_Character_AndItem_AreAccepted_UnknownIsRefused()
    {
        var ctx = Ctx();
        Assert.True((await Run(Attach("fixture:hitching-post"), ctx)).Success);
        Assert.True((await Run(Attach("chars/rider"), ctx)).Success);
        Assert.True((await Run(Attach("items/rope"), ctx)).Success);
        Assert.False((await Run(Attach("chars/nobody"), ctx)).Success);
        Assert.False((await Run(Attach("chars/horse"), ctx)).Success);
        Assert.Equal(3, _horse.SystemStats!.Tethers.Count);
    }

    [Fact]
    public async Task Attach_SameAnchorTwice_Replaces_AndCapsAtFour()
    {
        var ctx = Ctx();
        await Run(Attach("fixture:a", dc: 10), ctx);
        await Run(Attach("fixture:a", dc: 20), ctx);
        Assert.Equal(20, Assert.Single(_horse.SystemStats!.Tethers).BreakDc);
        for (var i = 0; i < 3; i++) await Run(Attach($"fixture:b{i}"), ctx);
        Assert.False((await Run(Attach("fixture:c"), ctx)).Success);
    }

    [Fact]
    public async Task Detach_RemovesOne_OrAll()
    {
        var ctx = Ctx();
        await Run(Attach("fixture:a"), ctx);
        await Run(Attach("fixture:b"), ctx);
        await Run(new TetherChange { Action = "detach", SubjectId = "chars/horse", AnchorId = "fixture:a" }, ctx);
        Assert.Equal("fixture:b", Assert.Single(_horse.SystemStats!.Tethers).AnchorId);
        await Run(new TetherChange { Action = "detach", SubjectId = "chars/horse" }, ctx);
        Assert.Empty(_horse.SystemStats.Tethers);
    }

    [Theory]
    [InlineData(12, 3, true)]   // 15 >= 15
    [InlineData(11, 3, false)]
    public async Task Strain_UsesCheckAgainstBreakDc(int d20, int bonus, bool frees)
    {
        var ctx = Ctx();
        await Run(Attach("fixture:post", dc: 15), ctx);
        var r = await Run(new TetherChange { Action = "strain", SubjectId = "chars/horse", D20 = d20, CheckBonus = bonus }, ctx);
        Assert.True(r.Success);
        Assert.Equal(frees ? 0 : 1, _horse.SystemStats!.Tethers.Count);
    }

    [Fact]
    public async Task Strain_WithoutTether_Fails()
    {
        Assert.False((await Run(new TetherChange { Action = "strain", SubjectId = "chars/horse", D20 = 10 }, Ctx())).Success);
    }

    [Fact]
    public async Task HolderIncapacitated_ReleasesTether()
    {
        var ctx = Ctx();
        await Run(Attach("items/rope", holder: "chars/rider"), ctx);
        Assert.Single(await TetherState.LiveAsync(ctx, _horse));

        _rider.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "unconscious", Category = "Condition" });
        Assert.Empty(await TetherState.LiveAsync(ctx, _horse));
        Assert.Empty(_horse.SystemStats!.Tethers);
        Assert.Contains(_summary, m => m.Contains("no longer tethered"));
    }

    [Fact]
    public async Task ExpiredHolderBlock_NoLongerHolds()
    {
        var ctx = Ctx();
        await Run(Attach("fixture:x", holder: "chars/rider"), ctx);
        // Blocked, but the block expired before "now" (day 0, hour 6 = 0.25): the holder can act, so it still holds.
        _rider.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "stunned", Category = "Condition", ExpiresAtDay = 0.1f });
        Assert.Single(await TetherState.LiveAsync(ctx, _horse));
    }

    [Fact]
    public async Task AnchorItemArchived_ReleasesTether()
    {
        var ctx = Ctx();
        await Run(Attach("items/rope"), ctx);
        _rope.IsArchived = true;
        Assert.Empty(await TetherState.LiveAsync(ctx, _horse));
    }

    [Fact]
    public async Task Travel_Blocked_UnlessAnchorOrHolderTravelsAlong()
    {
        var alone = Ctx(new TravelChange { CharacterId = "chars/horse", DestinationLocationId = "locations/town" });
        await Run(Attach("chars/rider", holder: "chars/rider"), alone);
        var t = _horse.SystemStats!.Tethers.Single();
        Assert.False(TetherState.AnchorTravelsAlong(alone, t, "locations/town"));

        var together = Ctx(
            new TravelChange { CharacterId = "chars/horse", DestinationLocationId = "locations/town" },
            new TravelChange { CharacterId = "chars/rider", DestinationLocationId = "locations/town" });
        Assert.True(TetherState.AnchorTravelsAlong(together, t, "locations/town"));

        var elsewhere = Ctx(
            new TravelChange { CharacterId = "chars/horse", DestinationLocationId = "locations/town" },
            new TravelChange { CharacterId = "chars/rider", DestinationLocationId = "locations/other" });
        Assert.False(TetherState.AnchorTravelsAlong(elsewhere, t, "locations/town"));

        _horse.SystemStats.Tethers.Clear();
        await Run(Attach("items/rope"), together); // rope is held by the rider, who travels
        Assert.True(TetherState.AnchorTravelsAlong(together, _horse.SystemStats.Tethers.Single(), "locations/town"));
        Assert.False(TetherState.AnchorTravelsAlong(alone, _horse.SystemStats.Tethers.Single(), "locations/town"));
    }

    [Fact]
    public async Task TravelHandler_RefusesATetheredSubject_ButNotOnceDetached()
    {
        _horse.SystemStats!.Tethers.Add(new Tether { AnchorId = "fixture:hitching-post", Label = "hitched" });
        var destination = new Location { Id = "loc_2", Name = "Road" };
        var dispatcher = new WorldChangeDispatcher(
            [new TravelChangeHandler(new EncounterResolver())], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [_horse.Id] = _horse },
            locations: new Dictionary<string, Location> { [destination.Id] = destination },
            dispatcher: dispatcher);

        var result = await new TravelChangeHandler(new EncounterResolver()).ApplyAsync(
            new TravelChange { CharacterId = _horse.Id, DestinationLocationId = "loc_2" }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("cannot travel", result.Message);
        Assert.Contains("hitching-post", result.Message);
    }
}
