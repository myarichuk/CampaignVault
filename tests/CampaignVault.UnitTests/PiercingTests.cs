using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PiercingTests
{
    private readonly Character _pc = new() { Id = "chars/ana", Name = "Ana", IsPc = true };
    private readonly List<string> _summary = [];

    private ChangeContext Ctx() => ChangeContextTestHelper.Create(
        characters: new() { [_pc.Id] = _pc },
        summary: _summary);

    private static Task<ChangeHandlerResult> Run(PiercingChange c, ChangeContext ctx) =>
        new PiercingChangeHandler().ApplyAsync(c, ctx, TestContext.Current.CancellationToken);

    private static PiercingChange Pierce(
        string action,
        string? site = null,
        string? kind = null,
        string? material = null,
        string? load = null,
        List<string>? tags = null,
        bool force = false,
        bool replaceTags = false) =>
        new()
        {
            CharacterId = "chars/ana",
            Action = action,
            Site = site,
            Kind = kind,
            Material = material,
            Load = load,
            Tags = tags,
            Force = force,
            ReplaceTags = replaceTags,
        };

    [Fact]
    public void Helpers_Add_Stacks_SameSiteKind_UnlessReplace()
    {
        List<PiercingMark> list = [];
        var added = PiercingHelpers.Add(list, "Ear.Lobe.Left", "Stud", day: 1, material: "Gold");
        Assert.Equal("added", added.Action);
        Assert.Equal("1", added.Mark!.Id);
        Assert.Equal("ear.lobe.left", Assert.Single(list).Site);
        Assert.Equal("gold", list[0].Material);

        var stacked = PiercingHelpers.Add(list, "ear.lobe.left", "stud", day: 2, load: "light", tags: ["bell"]);
        Assert.Equal("added", stacked.Action);
        Assert.Equal(2, list.Count);
        Assert.Equal("2", stacked.Mark!.Id);

        var updated = PiercingHelpers.Add(list, "ear.lobe.left", "stud", day: 3, load: "heavy", replace: true);
        Assert.Equal("updated", updated.Action);
        Assert.Equal(2, list.Count); // replace touches first match only
        Assert.Equal(PiercingLoads.Heavy, list[0].Load);
    }

    [Fact]
    public void Helpers_SameSite_ManyRings_And_Clit()
    {
        List<PiercingMark> list = [];
        PiercingHelpers.Add(list, "labia.left", PiercingKinds.Ring, 0);
        PiercingHelpers.Add(list, "labia.left", PiercingKinds.Ring, 0);
        PiercingHelpers.Add(list, "labia.left", PiercingKinds.Ring, 0);
        PiercingHelpers.Add(list, "clitoris", "lewd.clit_ring", 0, tags: [PiercingTags.LeashRing]);
        Assert.Equal(4, list.Count);
        Assert.Equal(3, list.Count(p => p.Site == "labia.left"));
        Assert.Equal(["1", "2", "3", "4"], list.Select(p => p.Id).ToList());

        var summary = PiercingHelpers.Summarize(list)!;
        Assert.Contains("3×", summary);
        Assert.Contains("clit_ring", summary);

        var (removed, _) = PiercingHelpers.Remove(list, site: null, kind: null, force: false, id: "2");
        Assert.Single(removed);
        Assert.Equal(3, list.Count);

        var ambiguous = PiercingHelpers.Update(list, "labia.left", PiercingKinds.Ring, day: 1, load: "heavy");
        Assert.NotNull(ambiguous.Error);
        Assert.Contains("pass id", ambiguous.Error);

        var one = PiercingHelpers.Update(list, null, null, day: 1, load: "heavy", id: "1");
        Assert.Equal("updated", one.Action);
        Assert.Equal(PiercingLoads.Heavy, list.Single(p => p.Id == "1").Load);
    }

    [Fact]
    public void Helpers_SameSiteDifferentKind_AreSeparate()
    {
        List<PiercingMark> list = [];
        PiercingHelpers.Add(list, PiercingSites.EarLobeLeft, PiercingKinds.Stud, 0);
        PiercingHelpers.Add(list, PiercingSites.EarLobeLeft, PiercingKinds.Hoop, 0);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Helpers_Remove_Locked_RequiresForce()
    {
        List<PiercingMark> list = [];
        PiercingHelpers.Add(list, PiercingSites.NoseSeptum, PiercingKinds.Ring, 0, tags: [PiercingTags.Locked]);
        var (removed, locked) = PiercingHelpers.Remove(list, PiercingSites.NoseSeptum, PiercingKinds.Ring, force: false);
        Assert.Empty(removed);
        Assert.Single(locked);
        Assert.Single(list);

        (removed, locked) = PiercingHelpers.Remove(list, PiercingSites.NoseSeptum, PiercingKinds.Ring, force: true);
        Assert.Single(removed);
        Assert.Empty(locked);
        Assert.Empty(list);
    }

    [Fact]
    public void Helpers_Evict_Oldest_WhenOverCap()
    {
        List<PiercingMark> list = [];
        for (var i = 0; i < PiercingHelpers.MaxPerHost; i++)
            PiercingHelpers.Add(list, $"ear.lobe.{i}", PiercingKinds.Stud, day: i);

        var outcome = PiercingHelpers.Add(list, "navel", PiercingKinds.Barbell, day: 99);
        Assert.Equal("added", outcome.Action);
        Assert.NotNull(outcome.Evicted);
        Assert.Equal(0, outcome.Evicted!.AppliedDay);
        Assert.Equal(PiercingHelpers.MaxPerHost, list.Count);
        Assert.Contains(list, p => p.Site == "navel");
    }

    [Fact]
    public void Helpers_Phrase_And_Summarize()
    {
        List<PiercingMark> list = [];
        PiercingHelpers.Add(list, PiercingSites.NoseSeptum, PiercingKinds.Ring, 1, material: "iron", load: "heavy",
            tags: [PiercingTags.Locked, PiercingTags.LeashRing]);
        PiercingHelpers.Add(list, PiercingSites.EarLobeLeft, PiercingKinds.Stud, 2, material: "gold");
        PiercingHelpers.Add(list, PiercingSites.Navel, PiercingKinds.Barbell, 3);
        PiercingHelpers.Add(list, PiercingSites.EarHelixRight, PiercingKinds.Hoop, 4);

        var phrase = PiercingHelpers.Phrase(list[0]);
        Assert.Contains("iron", phrase);
        Assert.Contains("ring", phrase);
        Assert.Contains("heavy", phrase);
        Assert.Contains("locked", phrase);

        var summary = PiercingHelpers.Summarize(list);
        Assert.NotNull(summary);
        Assert.Contains("+1 more", summary);
    }

    [Fact]
    public void Helpers_NamespacedKind_PhrasesLeaf()
    {
        var mark = new PiercingMark
        {
            Site = "nipple.left",
            Kind = "lewd.nipple_ring",
            Load = PiercingLoads.None,
        };
        Assert.Equal("nipple_ring at nipple left", PiercingHelpers.Phrase(mark));
    }

    [Fact]
    public async Task Handler_Add_PublishesPiercedEvent()
    {
        var ctx = Ctx();
        var result = await Run(Pierce("add", PiercingSites.Navel, PiercingKinds.Barbell, material: "silver"), ctx);
        Assert.True(result.Success);
        Assert.Single(_pc.Piercings);
        Assert.Contains(ctx.TakePendingEvents(), e => e.Topic == CoreEvents.Pierced);
        Assert.Contains(_summary, s => s.Contains("added", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handler_Remove_Locked_FailsWithoutForce()
    {
        var ctx = Ctx();
        Assert.True((await Run(Pierce("add", PiercingSites.NoseSeptum, PiercingKinds.Ring,
            tags: [PiercingTags.Locked]), ctx)).Success);

        var fail = await Run(Pierce("remove", PiercingSites.NoseSeptum, PiercingKinds.Ring), ctx);
        Assert.False(fail.Success);
        Assert.Single(_pc.Piercings);

        var ok = await Run(Pierce("remove", PiercingSites.NoseSeptum, PiercingKinds.Ring, force: true), ctx);
        Assert.True(ok.Success);
        Assert.Empty(_pc.Piercings);
    }

    [Fact]
    public async Task Handler_ClearSite_And_ClearAll()
    {
        var ctx = Ctx();
        await Run(Pierce("add", PiercingSites.EarLobeLeft, PiercingKinds.Stud), ctx);
        await Run(Pierce("add", PiercingSites.EarLobeLeft, PiercingKinds.Hoop), ctx);
        await Run(Pierce("add", PiercingSites.Navel, PiercingKinds.Barbell), ctx);

        Assert.True((await Run(Pierce("clear_site", PiercingSites.EarLobeLeft), ctx)).Success);
        Assert.Single(_pc.Piercings);
        Assert.Equal("navel", _pc.Piercings[0].Site);

        Assert.True((await Run(Pierce("clear_all"), ctx)).Success);
        Assert.Empty(_pc.Piercings);
    }

    [Fact]
    public async Task Handler_Update_MergesTags_UnlessReplace()
    {
        var ctx = Ctx();
        await Run(Pierce("add", PiercingSites.NoseSeptum, PiercingKinds.Ring, tags: ["fresh"]), ctx);
        await Run(Pierce("update", PiercingSites.NoseSeptum, PiercingKinds.Ring, load: "heavy", tags: ["bell"]), ctx);
        Assert.Contains("fresh", _pc.Piercings[0].Tags);
        Assert.Contains("bell", _pc.Piercings[0].Tags);
        Assert.Equal(PiercingLoads.Heavy, _pc.Piercings[0].Load);

        await Run(Pierce("update", PiercingSites.NoseSeptum, PiercingKinds.Ring, tags: ["locked"], replaceTags: true), ctx);
        Assert.Equal(["locked"], _pc.Piercings[0].Tags);
    }
}
