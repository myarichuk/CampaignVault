using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class SoilTests
{
    private readonly Character _pc = new() { Id = "chars/ana", Name = "Ana", IsPc = true };
    private readonly Item _boots = new() { Id = "items/boots", Name = "Boots", HolderId = "chars/ana" };
    private readonly Location _tavern = new() { Id = "locations/tavern", Name = "The Rusty Nail" };
    private readonly List<string> _summary = [];

    private ChangeContext Ctx() => ChangeContextTestHelper.Create(
        characters: new() { [_pc.Id] = _pc },
        items: new() { [_boots.Id] = _boots },
        locations: new() { [_tavern.Id] = _tavern },
        summary: _summary);

    private static Task<ChangeHandlerResult> Run(SoilChange c, ChangeContext ctx) =>
        new SoilChangeHandler().ApplyAsync(c, ctx, TestContext.Current.CancellationToken);

    private static SoilChange Soil(string target, string? kind, int amount = 1, string? spot = null, string? fixture = null, bool? clear = null) =>
        new() { TargetId = target, Kind = kind, Amount = amount, Spot = spot, Fixture = fixture, Clear = clear };

    // ---- helpers ------------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_AddsThenWorsens_AndClampsAtHeavy()
    {
        List<DirtMark> dirt = [];
        SoilHelpers.Apply(dirt, "Mud", "boots", null, 1, day: 3);
        SoilHelpers.Apply(dirt, " MUD ", "Boots", null, 1, day: 4);
        SoilHelpers.Apply(dirt, "mud", "boots", null, 5, day: 5);

        var mark = Assert.Single(dirt);
        Assert.Equal("mud", mark.Kind);
        Assert.Equal(DirtMark.MaxSeverity, mark.Severity);
        Assert.Equal(5, mark.AppliedDay);
    }

    [Fact]
    public void Apply_SameKindDifferentSpotOrFixture_AreSeparateMarks()
    {
        List<DirtMark> dirt = [];
        SoilHelpers.Apply(dirt, "blood", "hands", null, 1, 0);
        SoilHelpers.Apply(dirt, "blood", "boots", null, 1, 0);
        SoilHelpers.Apply(dirt, "blood", null, "floor", 1, 0);
        SoilHelpers.Apply(dirt, "blood", null, "north wall", 1, 0);
        Assert.Equal(4, dirt.Count);
    }

    [Fact]
    public void Apply_Negative_LightensEveryMatch_AndDropsAtZero()
    {
        List<DirtMark> dirt = [];
        SoilHelpers.Apply(dirt, "mud", "boots", null, 2, 0);
        SoilHelpers.Apply(dirt, "mud", "hem", null, 1, 0);
        SoilHelpers.Apply(dirt, "blood", "hands", null, 1, 0);

        var outcome = SoilHelpers.Apply(dirt, "mud", null, null, -1, 0);

        Assert.Equal("cleaned", outcome.Action);
        Assert.Equal(2, outcome.Changed.Count);
        Assert.Equal(["blood", "mud"], dirt.Select(d => d.Kind).OrderBy(k => k));
        Assert.Equal(1, dirt.Single(d => d.Kind == "mud").Severity);
    }

    [Fact]
    public void Apply_PastCap_EvictsLowestSeverityThenOldest()
    {
        List<DirtMark> dirt = [];
        for (var i = 0; i < SoilHelpers.MaxPerHost; i++)
            SoilHelpers.Apply(dirt, $"k{i}", null, null, i == 0 ? 3 : 1, day: i);

        var outcome = SoilHelpers.Apply(dirt, "fresh", null, null, 1, day: 20);

        Assert.Equal(SoilHelpers.MaxPerHost, dirt.Count);
        Assert.Equal("k1", outcome.Evicted?.Kind); // k0 is heavy, k1 is the oldest light mark
        Assert.Contains(dirt, d => d.Kind == "fresh");
        Assert.Contains(dirt, d => d.Kind == "k0");
    }

    [Fact]
    public void Apply_PastCap_WithNothingLight_StillSucceeds()
    {
        List<DirtMark> dirt = [];
        for (var i = 0; i < SoilHelpers.MaxPerHost; i++)
            SoilHelpers.Apply(dirt, $"k{i}", null, null, 3, day: i);

        var outcome = SoilHelpers.Apply(dirt, "fresh", null, null, 1, day: 20);

        Assert.Equal("k0", outcome.Evicted?.Kind);
        Assert.Equal(SoilHelpers.MaxPerHost, dirt.Count);
    }

    [Fact]
    public void Clear_MatchesFilters_AndNoFiltersEmptiesTheHost()
    {
        List<DirtMark> dirt = [];
        SoilHelpers.Apply(dirt, "mud", "boots", null, 1, 0);
        SoilHelpers.Apply(dirt, "blood", "boots", null, 1, 0);
        SoilHelpers.Apply(dirt, "blood", "hands", null, 1, 0);

        Assert.Single(SoilHelpers.Clear(dirt, "blood", "hands", null));
        Assert.Equal(2, dirt.Count);
        Assert.Equal(2, SoilHelpers.Clear(dirt, null, null, null).Count);
        Assert.Empty(dirt);
    }

    [Fact]
    public void Summarize_IsNullWhenClean_ShowsTwoHeaviest_AndCountsTheRest()
    {
        Assert.Null(SoilHelpers.Summarize(null));
        Assert.Null(SoilHelpers.Summarize([]));

        List<DirtMark> dirt = [];
        SoilHelpers.Apply(dirt, "mud", "boots", null, 2, 1);
        SoilHelpers.Apply(dirt, "blood", null, null, 3, 1);
        SoilHelpers.Apply(dirt, "dust", "cloak", null, 1, 1);
        SoilHelpers.Apply(dirt, "myplugin.ichor", "hem", null, 1, 1);

        Assert.Equal("heavily bloodied, muddy boots, +2 more", SoilHelpers.Summarize(dirt));
    }

    [Theory]
    [InlineData("blood", null, null, 2, "bloodied")]
    [InlineData("blood", "hands", null, 2, "bloody hands")]
    [InlineData("scorch", null, "north wall", 3, "heavily scorched north wall")]
    [InlineData("notches", "leg", null, 1, "slightly notched leg")]
    [InlineData("debris", null, "floor", 2, "littered floor")]
    [InlineData("myplugin.ichor", "hem", null, 2, "myplugin.ichor-stained hem")]
    public void Phrase_ReadsLikeNarration(string kind, string? spot, string? fixture, int severity, string expected) =>
        Assert.Equal(expected, SoilHelpers.Phrase(new DirtMark { Kind = kind, Spot = spot, Fixture = fixture, Severity = severity }));

    [Fact]
    public void HostExtensions_ReadDirtOnAnyHost()
    {
        IHasDirt host = _pc;
        Assert.False(host.HasDirt());
        SoilHelpers.Apply(_pc.Dirt, "blood", null, null, 2, 0);
        Assert.True(host.HasDirt());
        Assert.True(host.HasDirt("BLOOD"));
        Assert.False(host.HasDirt("mud"));
        Assert.Equal(2, host.SeverityOf("blood"));
        Assert.Equal(0, host.SeverityOf("mud"));
    }

    // ---- handler ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Handler_AddsStacksWashesAndClears_OnACharacter()
    {
        var ctx = Ctx();
        Assert.True((await Run(Soil(_pc.Id, "Blood", spot: "hands"), ctx)).Success);
        Assert.True((await Run(Soil(_pc.Id, "blood", spot: "HANDS"), ctx)).Success);
        Assert.Equal(2, Assert.Single(_pc.Dirt).Severity);

        Assert.True((await Run(Soil(_pc.Id, "blood", amount: -1), ctx)).Success);
        Assert.Equal(1, Assert.Single(_pc.Dirt).Severity);

        Assert.True((await Run(Soil(_pc.Id, "blood", amount: -5), ctx)).Success);
        Assert.Empty(_pc.Dirt);

        await Run(Soil(_pc.Id, "mud", spot: "boots"), ctx);
        await Run(Soil(_pc.Id, "dust"), ctx);
        Assert.True((await Run(Soil(_pc.Id, null, clear: true), ctx)).Success);
        Assert.Empty(_pc.Dirt);
    }

    [Fact]
    public async Task Handler_Clear_WithKind_OnlyRemovesThatKind()
    {
        var ctx = Ctx();
        await Run(Soil(_pc.Id, "mud", spot: "boots"), ctx);
        await Run(Soil(_pc.Id, "blood"), ctx);
        await Run(Soil(_pc.Id, "mud", clear: true), ctx);
        Assert.Equal("blood", Assert.Single(_pc.Dirt).Kind);
    }

    [Fact]
    public async Task Handler_WashingNothing_IsANoOpSuccess()
    {
        var result = await Run(Soil(_pc.Id, "mud", amount: -1), Ctx());
        Assert.True(result.Success);
        Assert.Contains(_summary, m => m.Contains("no matching dirt"));
    }

    [Fact]
    public async Task Handler_WorksOnItems()
    {
        await Run(Soil(_boots.Id, "mud", amount: 2), Ctx());
        Assert.Equal(2, Assert.Single(_boots.Dirt).Severity);
    }

    [Fact]
    public async Task Handler_Location_FixturesScopeTheMarks()
    {
        var ctx = Ctx();
        await Run(Soil(_tavern.Id, "scorch", fixture: "north wall"), ctx);
        await Run(Soil(_tavern.Id, "scorch", fixture: "south wall"), ctx);
        await Run(Soil(_tavern.Id, "debris", fixture: "floor", amount: 2), ctx);
        Assert.Equal(3, _tavern.Dirt.Count);

        await Run(Soil(_tavern.Id, "scorch", amount: -1, fixture: "north wall"), ctx);

        Assert.DoesNotContain(_tavern.Dirt, d => d.Fixture == "north wall");
        Assert.Contains(_tavern.Dirt, d => d.Fixture == "south wall");
        Assert.Contains(_tavern.Dirt, d => d.Kind == "debris");
    }

    [Fact]
    public async Task Handler_FixtureOnACharacter_IsRefused()
    {
        var result = await Run(Soil(_pc.Id, "mud", fixture: "floor"), Ctx());
        Assert.False(result.Success);
        Assert.Empty(_pc.Dirt);
    }

    [Theory]
    [InlineData("chars/nobody")]
    [InlineData("factions/guild")]
    [InlineData("")]
    public async Task Handler_UnknownTarget_Fails(string target) =>
        Assert.False((await Run(Soil(target, "mud"), Ctx())).Success);

    [Fact]
    public async Task Handler_RejectsMissingKind_ZeroAmount_AndLongKind()
    {
        var ctx = Ctx();
        Assert.False((await Run(Soil(_pc.Id, null), ctx)).Success);
        Assert.False((await Run(Soil(_pc.Id, "mud", amount: 0), ctx)).Success);
        Assert.False((await Run(Soil(_pc.Id, new string('x', SoilHelpers.MaxKindLength + 1)), ctx)).Success);
        Assert.Empty(_pc.Dirt);
    }

    [Fact]
    public async Task Handler_PastTheCap_Evicts_AndSaysSo()
    {
        var ctx = Ctx();
        for (var i = 0; i < SoilHelpers.MaxPerHost; i++)
            await Run(Soil(_pc.Id, $"kind{i}"), ctx);

        var result = await Run(Soil(_pc.Id, "fresh"), ctx);

        Assert.True(result.Success);
        Assert.Equal(SoilHelpers.MaxPerHost, _pc.Dirt.Count);
        Assert.Contains(_summary, m => m.Contains("faded to make room"));
        Assert.Contains(ctx.TakePendingEvents(), e => e.TryGet<string>(CoreEvents.Fields.Action, out var a) && a == "evicted");
    }

    [Fact]
    public async Task Handler_PublishesSoiledEvent_WithFields()
    {
        var ctx = Ctx();
        await Run(Soil(_pc.Id, "Mud", spot: "boots"), ctx);
        await Run(Soil(_tavern.Id, "scorch", fixture: "north wall", amount: 2), ctx);
        await Run(Soil(_pc.Id, "mud", amount: -1), ctx);

        var events = ctx.TakePendingEvents().Where(e => e.Topic == CoreEvents.Soiled).ToList();
        Assert.Equal(3, events.Count);

        Assert.True(events[0].TryGet<string>(CoreEvents.Fields.TargetId, out var target));
        Assert.Equal(_pc.Id, target);
        Assert.True(events[0].TryGet<string>(CoreEvents.Fields.Kind, out var kind));
        Assert.Equal("mud", kind);
        Assert.True(events[0].TryGet<int>(CoreEvents.Fields.Severity, out var severity));
        Assert.Equal(1, severity);
        Assert.True(events[0].TryGet<string>(CoreEvents.Fields.Spot, out var spot));
        Assert.Equal("boots", spot);
        Assert.True(events[0].TryGet<string>(CoreEvents.Fields.Action, out var action));
        Assert.Equal("applied", action);

        Assert.True(events[1].TryGet<string>(CoreEvents.Fields.Fixture, out var fixture));
        Assert.Equal("north wall", fixture);

        Assert.True(events[2].TryGet<string>(CoreEvents.Fields.Action, out var cleaned));
        Assert.Equal("cleaned", cleaned);
        Assert.True(events[2].TryGet<int>(CoreEvents.Fields.Severity, out var gone));
        Assert.Equal(0, gone);
    }

    [Fact]
    public void ExtractInvolvedEntities_UsesOnlyTargetId()
    {
        var chars = new HashSet<string>();
        var locs = new HashSet<string>();
        var quests = new HashSet<string>();
        var items = new HashSet<string>();
        var all = new HashSet<string>();

        // "locket", "quicksilver" and "item..." would look like IDs to the reflection-based default extractor.
        var change = new SoilChange { TargetId = "chars/ana", Kind = "quicksilver", Spot = "locket", Note = "item of note" };
        var supported = new SoilChangeHandler().ExtractInvolvedEntities(change, chars, locs, null, quests, items, all);

        Assert.True(supported);
        Assert.Equal(["chars/ana"], chars);
        Assert.Equal(["chars/ana"], all);
        Assert.Empty(locs);
        Assert.Empty(quests);
        Assert.Empty(items);
    }

    [Fact]
    public async Task Handler_FallsBackToSession_ButOnlyWithinTheCampaign()
    {
        var mine = new Character { Id = "chars/mine", Name = "Mine", CampaignName = "camp" };
        var theirs = new Character { Id = "chars/theirs", Name = "Theirs", CampaignName = "other" };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>("chars/mine", Arg.Any<CancellationToken>()).Returns(mine);
        session.LoadAsync<Character>("chars/theirs", Arg.Any<CancellationToken>()).Returns(theirs);
        var ctx = ChangeContextTestHelper.Create(session: session, campaignName: "camp");

        Assert.True((await Run(Soil("chars/mine", "mud"), ctx)).Success);
        Assert.False((await Run(Soil("chars/theirs", "mud"), ctx)).Success);
        Assert.Single(mine.Dirt);
        Assert.Empty(theirs.Dirt);
    }

    // ---- projections --------------------------------------------------------------------------------------------

    [Fact]
    public void CharacterView_CleanHost_CarriesNothing()
    {
        var view = CharacterDetailView.From(_pc);
        Assert.Null(view.Soil);
        Assert.Null(view.Dirt);
        Assert.Null(CharacterDetailView.From(_pc, includeDirtDetail: true).Dirt);
    }

    [Fact]
    public void CharacterView_ListShape_IsOneCompactLine_FullDetailIsTheList()
    {
        SoilHelpers.Apply(_pc.Dirt, "mud", "boots", null, 2, 0);
        SoilHelpers.Apply(_pc.Dirt, "blood", null, null, 3, 0);
        SoilHelpers.Apply(_pc.Dirt, "dust", "cloak", null, 1, 0);

        var compact = CharacterDetailView.From(_pc);
        Assert.Equal("heavily bloodied, muddy boots, +1 more", compact.Soil);
        Assert.Null(compact.Dirt);

        var full = CharacterDetailView.From(_pc, includeDirtDetail: true);
        Assert.Null(full.Soil);
        Assert.Equal(3, full.Dirt?.Count);
    }

    [Fact]
    public void ItemView_ShowsCompactSoil_OrNothing()
    {
        Assert.Null(ItemSummaryView.From(_boots).Soil);
        SoilHelpers.Apply(_boots.Dirt, "mud", null, null, 2, 0);
        Assert.Equal("muddy", ItemSummaryView.From(_boots).Soil);
    }

    [Fact]
    public void LocationView_SceneShapeIsCompact_FullDescriptionAddsTheList()
    {
        Assert.Null(LocationDetailView.From(_tavern).Soil);

        SoilHelpers.Apply(_tavern.Dirt, "scorch", null, "north wall", 2, 0);
        SoilHelpers.Apply(_tavern.Dirt, "debris", null, "floor", 1, 0);

        var scene = LocationDetailView.From(_tavern);
        Assert.Equal("scorched north wall, slightly littered floor", scene.Soil);
        Assert.Null(scene.Dirt);

        var full = LocationDetailView.From(_tavern, fullDescription: true);
        Assert.Null(full.Soil);
        Assert.Equal(2, full.Dirt?.Count);
    }

    // ---- schema, events, and the out-of-tree plugin path -------------------------------------------------------

    [Fact]
    public void SoilVerb_IsInTheCommitIndex_NotHotTier_AndWorldCategory()
    {
        var variant = CommitSchemaModel.Find("soil");
        Assert.NotNull(variant);
        Assert.False(variant!.IsHotTier);
        Assert.False(variant.IsEngineOnly);
        Assert.Equal("World", variant.Category);
        Assert.Contains(CommitSchemaRegistry.GetIndex(), s => s.Type == "soil");
        Assert.Contains("same batch", variant.Summary);
    }

    [Fact]
    public void SoilChange_HasAParameterlessConstructor_ForStartupCoverageValidation() =>
        Assert.NotNull(Activator.CreateInstance(typeof(SoilChange)));

    [Fact]
    public void SoilChange_RoundTripsAsJson_WithTheDiscriminator()
    {
        var json = """{"$type":"soil","targetId":"chars/ana","kind":"mud","spot":"boots"}""";
        var change = System.Text.Json.JsonSerializer.Deserialize<WorldChange>(json);
        var soil = Assert.IsType<SoilChange>(change);
        Assert.Equal(1, soil.Amount);
        Assert.Equal("boots", soil.Spot);
    }

    private sealed class ReactingSubscriber(string topic, Func<DomainEvent, IReadOnlyList<WorldChange>>? react = null) : IDomainEventHandler
    {
        public List<DomainEvent> Received { get; } = [];
        public IReadOnlyCollection<string> Topics => [topic];

        public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
        {
            Received.Add(e);
            return Task.FromResult(react?.Invoke(e) ?? []);
        }
    }

    [Fact]
    public async Task Plugin_CanInventAKind_AndAnotherPluginHearsAboutIt()
    {
        // An out-of-tree plugin: reacts to a core event by returning a SoilChange with a kind core has never heard of.
        var ghost = new ReactingSubscriber(CoreEvents.CombatEnded, _ =>
            [new SoilChange { TargetId = "chars/ana", Kind = "myplugin.ectoplasm", Spot = "hands", Amount = 2 }]);
        // A second plugin listens to core.soiled.v1 and reads the host's dirt from the context.
        var tracker = new ReactingSubscriber(CoreEvents.Soiled);
        var dispatcher = new WorldChangeDispatcher([new SoilChangeHandler()], new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance, eventHandlers: [ghost, tracker]);

        var result = await dispatcher.PublishAsync(Substitute.For<IAsyncDocumentSession>(), "test",
            [(CoreEvents.CombatEnded, null)], [_pc], null,
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        var mark = Assert.Single(_pc.Dirt);
        Assert.Equal("myplugin.ectoplasm", mark.Kind);
        Assert.Equal(2, mark.Severity);
        Assert.Equal("hands", mark.Spot);
        Assert.True(_pc.HasDirt("myplugin.ectoplasm"));

        var heard = Assert.Single(tracker.Received);
        Assert.True(heard.TryGet<string>(CoreEvents.Fields.Kind, out var kind));
        Assert.Equal("myplugin.ectoplasm", kind);
    }
}
