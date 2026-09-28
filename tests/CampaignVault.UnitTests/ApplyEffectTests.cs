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

public class ApplyEffectTests
{
    private readonly Character _pc = new() { Id = "chars/pc", Name = "Vess", IsPc = true, SystemStats = new SystemExtension() };
    private readonly List<string> _summary = [];

    private ChangeContext Ctx()
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(null, new() { [_pc.Id] = _pc }, [], [], [], [], NullLogger.Instance, _summary, dispatcher);
    }

    private static ApplyEffectChange Req(string key, string valence, string tier, float mod, double? hours = 1, string stat = "AllChecks") => new()
    {
        CharacterId = "chars/pc", Key = key, Name = key, Valence = valence, Tier = tier,
        Modifiers = new() { [stat] = mod }, DurationHours = hours
    };

    private async Task<ChangeHandlerResult> Apply(ApplyEffectChange change, ChangeContext? ctx = null) =>
        await new ApplyEffectChangeHandler().ApplyAsync(change, ctx ?? Ctx(), TestContext.Current.CancellationToken);

    private List<StatusEffect> Effects => _pc.SystemStats!.StatusEffects;

    [Fact]
    public async Task Light_ClampsMagnitudeAndDuration_AndSaysSo()
    {
        var r = await Apply(Req("mood", "buff", "light", 3, hours: 5));
        Assert.True(r.Success);
        var e = Assert.Single(Effects);
        Assert.Equal(1f, e.StatModifiers["AllChecks"]);
        Assert.Equal(1f / 24f, e.ExpiresAtDay!.Value - 6f / 24f, 3);
        Assert.Contains(_summary, m => m.Contains("clamped"));
        Assert.Equal("light", e.EffectTier);
    }

    [Theory]
    [InlineData("buff", -1f)]
    [InlineData("debuff", 1f)]
    public async Task WrongSign_IsRefused(string valence, float mod)
    {
        Assert.False((await Apply(Req("x", valence, "light", mod))).Success);
        Assert.Empty(Effects);
    }

    [Fact]
    public async Task UnknownStat_AndMissingDuration_AreRefused()
    {
        Assert.False((await Apply(Req("x", "buff", "light", 1, stat: "Charisma-ish"))).Success);
        Assert.False((await Apply(Req("x", "buff", "light", 1, hours: null))).Success);
        Assert.Empty(Effects);
    }

    [Fact]
    public async Task Speed_NeedsModerate_AndSeriousNeedsRecoveryHint()
    {
        Assert.False((await Apply(Req("s", "debuff", "light", -5, stat: "Speed"))).Success);
        Assert.True((await Apply(Req("s", "debuff", "moderate", -30, hours: 4, stat: "Speed"))).Success);
        Assert.Equal(-10f, Effects.Single().StatModifiers["Speed"]);
        Assert.False((await Apply(Req("bad", "debuff", "serious", -3, hours: 12))).Success);
        var withHint = Req("bad", "debuff", "serious", -3, hours: 48);
        withHint.RecoveryHint = "rest";
        Assert.True((await Apply(withHint)).Success);
        Assert.Equal(1f, Effects.Single(e => e.EffectKey == "bad").ExpiresAtDay!.Value - 6f / 24f, 3);
    }

    [Fact]
    public async Task Persistent_NeedsSourceAndRemoval_AndNeverExpires()
    {
        var curse = Req("curse", "debuff", "persistent", -2, hours: null);
        Assert.False((await Apply(curse)).Success);
        curse.ImposedBy = "chars/hag";
        curse.Removal = "remove curse";
        Assert.True((await Apply(curse)).Success);
        var e = Assert.Single(Effects);
        Assert.Null(e.ExpiresAtDay);
        Assert.Equal("chars/hag", e.AppliedBy);
        Assert.Equal("Curse", e.Category);
    }

    [Fact]
    public async Task SameKey_Refreshes_ToStrongerValue_WithoutStacking()
    {
        var ctx = Ctx();
        await Apply(Req("mood", "buff", "light", 1, hours: 1), ctx);
        await Apply(Req("mood", "buff", "moderate", 2, hours: 4), ctx);
        var e = Assert.Single(Effects);
        Assert.Equal(2f, e.StatModifiers["AllChecks"]);
        Assert.Equal("moderate", e.EffectTier);
        Assert.Equal(4f / 24f, e.ExpiresAtDay!.Value - 6f / 24f, 3);

        await Apply(Req("mood", "buff", "light", 1, hours: 1), ctx);
        Assert.Equal(2f, Assert.Single(Effects).StatModifiers["AllChecks"]);
    }

    [Fact]
    public async Task ThirdBuff_IsNotApplied_ButDebuffsHaveTheirOwnCap()
    {
        var ctx = Ctx();
        await Apply(Req("a", "buff", "light", 1), ctx);
        await Apply(Req("b", "buff", "light", 1), ctx);
        var third = await Apply(Req("c", "buff", "light", 1), ctx);
        Assert.True(third.Success);
        Assert.Equal(2, Effects.Count);
        Assert.Contains(_summary, m => m.Contains("NOT applied"));

        await Apply(Req("d", "debuff", "light", -1), ctx);
        Assert.Equal(3, Effects.Count);
    }

    [Fact]
    public async Task TimeSweep_RemovesExpiredEffects_KeepsTheRest()
    {
        var ctx = Ctx();
        await Apply(Req("short", "buff", "light", 1, hours: 1), ctx);
        await Apply(Req("long", "debuff", "moderate", -1, hours: 8), ctx);
        Effects.Add(new StatusEffect { Name = "Legacy", Category = "x", ExpiresAtDay = 0.1f });

        // 6h clock start + 3h later: the 1h effect is gone, the 8h one is not, legacy effects are not touched.
        var span = new TimeAdvancedChange { Hours = 3, TotalHoursAfter = 9, CharacterIds = [_pc.Id] };
        await new TimeAdvancedChangeHandler().ApplyAsync(span, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(["long", "Legacy"], Effects.Select(e => e.Name));
    }

    [Fact]
    public async Task RestSteps_GiveBackDrainedWillpower_ButTravelDoesNot()
    {
        var ctx = Ctx();
        CampaignVault.Rulesets.WillpowerRules.Apply(_pc.SystemStats, -12, isDelta: true);

        await new TimeAdvancedChangeHandler().ApplyAsync(
            new TimeAdvancedChange { Source = "travel", Hours = 4, TotalHoursAfter = 10, CharacterIds = [_pc.Id] }, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(63f, _pc.SystemStats.Willpower);

        await new TimeAdvancedChangeHandler().ApplyAsync(
            new TimeAdvancedChange { Source = "rest", Hours = 4, TotalHoursAfter = 14, CharacterIds = [_pc.Id] }, ctx, TestContext.Current.CancellationToken);
        Assert.Equal(68f, _pc.SystemStats.Willpower);
        Assert.Contains(_summary, m => m.Contains("recovers 5 willpower"));
    }
}
