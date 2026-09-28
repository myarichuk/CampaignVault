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

public class ConsequenceBeatTests
{
    private readonly Character _pc = new() { Id = "chars/pc", Name = "Vess", IsPc = true, SystemStats = new SystemExtension() };
    private readonly List<string> _summary = [];

    private ChangeContext Ctx(Dictionary<string, string>? options = null)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(null, new() { [_pc.Id] = _pc }, [], [], [], [], NullLogger.Instance, _summary, dispatcher);
        return ctx;
    }

    private static TimeAdvance Step(string terrain = "mountains", double hours = 6, double total = 100, string source = "travel") =>
        new(source, hours, total, ["chars/pc"], "locations/x", terrain);

    private static Task Fire(ConsequenceBeatObserver o, TimeAdvance a, ChangeContext c) =>
        o.OnTimeAdvancedAsync(a, c, TestContext.Current.CancellationToken);

    [Fact]
    public void Settings_DefaultToLightWithEightAndTwentyFourHourCooldowns()
    {
        var s = ConsequenceBeats.Read(null);
        Assert.Equal(ConsequenceBeats.Mode.Light, s.Mode);
        Assert.Equal(8, s.GoodCooldownHours);
        Assert.Equal(24, s.BadCooldownHours);
        Assert.Equal(2, s.MaxPerDay);
    }

    [Fact]
    public void Settings_OffMeansNoChance_AndOverridesApply()
    {
        var off = ConsequenceBeats.Read(new Dictionary<string, string> { ["consequences"] = "off" });
        Assert.Equal(0, ConsequenceBeats.Chance(off, Step()));

        var s = ConsequenceBeats.Read(new Dictionary<string, string>
        {
            ["consequences"] = "full", ["consequenceCooldownHours"] = "4", ["consequenceMaxPerDay"] = "5"
        });
        Assert.Equal(4, s.GoodCooldownHours);
        Assert.Equal(12, s.BadCooldownHours);
        Assert.Equal(5, s.MaxPerDay);
    }

    [Fact]
    public void Chance_IsHigherInHarshTerrain_AndFullBeatsLight()
    {
        var light = ConsequenceBeats.Read(null);
        var full = ConsequenceBeats.Read(new Dictionary<string, string> { ["consequences"] = "full" });
        Assert.True(ConsequenceBeats.Chance(light, Step("mountains")) > ConsequenceBeats.Chance(light, Step("road")));
        Assert.True(ConsequenceBeats.Chance(full, Step()) > ConsequenceBeats.Chance(light, Step()));
        Assert.True(ConsequenceBeats.Chance(full, Step(hours: 3)) < ConsequenceBeats.Chance(full, Step(hours: 6)));
    }

    [Fact]
    public void Decide_HarshTerrainLeansBad_LightModeNeverSerious_GoodNeverSerious()
    {
        var light = ConsequenceBeats.Read(null);
        var full = ConsequenceBeats.Read(new Dictionary<string, string> { ["consequences"] = "full" });
        Assert.Equal("bad", ConsequenceBeats.Decide(light, ConsequenceBeats.TerrainClass.Harsh, 0.5, 0).Valence);
        Assert.Equal("good", ConsequenceBeats.Decide(light, ConsequenceBeats.TerrainClass.Safe, 0.5, 0).Valence);
        Assert.Equal("moderate", ConsequenceBeats.Decide(light, ConsequenceBeats.TerrainClass.Harsh, 0.1, 0.99).Severity);
        Assert.Equal("serious", ConsequenceBeats.Decide(full, ConsequenceBeats.TerrainClass.Harsh, 0.1, 0.99).Severity);
        Assert.Equal("moderate", ConsequenceBeats.Decide(full, ConsequenceBeats.TerrainClass.Safe, 0.1 + 0.25, 0.99).Severity);
    }

    [Fact]
    public async Task Observer_RollHit_EmitsHint_AndStartsCooldown()
    {
        var o = new ConsequenceBeatObserver(() => 0.0); // always passes the chance roll; valence 0 -> bad in harsh terrain
        var ctx = Ctx();
        await Fire(o, Step(total: 100), ctx);
        Assert.Contains(_summary, m => m.Contains("CONSEQUENCE BEAT") && m.Contains("bad"));

        // Bad cooldown is 24h: the next bad beat 6h later is suppressed.
        _summary.Clear();
        await Fire(o, Step(total: 106), ctx);
        Assert.Empty(_summary);
        await Fire(o, Step(total: 124), ctx);
        Assert.Contains(_summary, m => m.Contains("CONSEQUENCE BEAT"));
    }

    [Fact]
    public async Task Observer_RollMiss_AndOffAndNonParty_DoNothing()
    {
        var ctx = Ctx();
        await Fire(new ConsequenceBeatObserver(() => 0.99), Step(), ctx);
        Assert.Empty(_summary);

        var off = new Dictionary<string, string> { ["consequences"] = "off" };
        _pc.SystemStats!.Traits.Clear();
        var offCtx = Ctx(off);
        // options come from the context's system options; the null-session test context has none, so exercise Read directly.
        Assert.Equal(0, ConsequenceBeats.Chance(ConsequenceBeats.Read(off), Step()));

        var npc = new Character { Id = "chars/npc", Name = "Bo", SystemStats = new SystemExtension() };
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var npcCtx = new ChangeContext(null, new() { [npc.Id] = npc }, [], [], [], [], NullLogger.Instance, _summary, dispatcher);
        await new ConsequenceBeatObserver(() => 0.0).OnTimeAdvancedAsync(
            new TimeAdvance("travel", 6, 100, [npc.Id], null, "mountains"), npcCtx, TestContext.Current.CancellationToken);
        Assert.Empty(_summary);
    }

    [Fact]
    public async Task DailyCap_StopsAThirdBeat_ThenResetsNextDay()
    {
        var s = ConsequenceBeats.Read(new Dictionary<string, string> { ["consequenceCooldownHours"] = "0" });
        var o = ConsequenceBeats.Allowed(s, _pc, new BeatOutcome("good", "light"), 100);
        Assert.True(o);
        ConsequenceBeats.Record(s, _pc, new BeatOutcome("good", "light"), 100);
        ConsequenceBeats.Record(s, _pc, new BeatOutcome("good", "light"), 101);
        Assert.False(ConsequenceBeats.Allowed(s, _pc, new BeatOutcome("good", "light"), 102));
        Assert.True(ConsequenceBeats.Allowed(s, _pc, new BeatOutcome("good", "light"), 24 * 5));
        await Task.CompletedTask;
    }
}
