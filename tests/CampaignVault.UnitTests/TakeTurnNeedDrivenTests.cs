using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Context;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// T6 need-driven take_turn (TAKE_TURN_PLAN.md): lean scene rosters, NPC cards once per session
/// (spotlight on arrival, anyone else on first involvement), context lines pushed only on the beat that
/// needs them, and the TurnCursor delivery ledger that keeps any of it from being re-sent.
/// </summary>
[Collection("RavenDB")]
public class TakeTurnNeedDrivenTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public TakeTurnNeedDrivenTests(RavenDBFixture fixture) => _fixture = fixture;

    private sealed record Market(string Slug, CampaignTools Tools, string LocId, string PcId, string OdaId, string FennId, string RopeId);

    /// <summary>A market with a PC (10 gold), Oda (friendly toward the PC: spotlight) and Fenn (a bystander).</summary>
    private async Task<Market> SeedMarketAsync(Func<Raven.Client.Documents.Session.IAsyncDocumentSession, string, Task>? extra = null)
    {
        var slug = "needdriven-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-market";
        var pcId = $"chars/{slug}-pc";
        var odaId = $"chars/{slug}-oda";
        var fennId = $"chars/{slug}-fenn";
        var ropeId = $"items/{slug}-rope";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId, Name = "Market", Description = "Stalls of fish, rope and lamp oil under striped awnings."
            });
            var pcStats = new Dnd5eExtension { ArmorClass = 12 };
            pcStats.ResourcePools["gold"] = new ResourcePool { Current = 10, Max = 1000 };
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = pcId, Name = "Tamsin", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10, SystemStats = pcStats
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = odaId, Name = "Oda", CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                Notes = "Harbormaster; keeps the ledger of every ship in port.",
                Psychology = new PsychologyProfile { Traits = ["dry humor"], Wants = ["an honest ledger"], Fears = ["the dark lighthouse"] },
                Social = new SocialProfile { Relationships = new Dictionary<string, int> { [pcId] = 65 } }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = fennId, Name = "Fenn", CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                Notes = "Sells rope and lanterns; has sold a great deal of lamp oil to strangers this week, and wonders why.",
                Psychology = new PsychologyProfile { Traits = ["gruff"], Wants = ["a quiet stall"] },
                SystemStats = new Dnd5eExtension { ArmorClass = 11 }
            });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = ropeId, Name = "Rope", Description = "Fifty feet of hemp.", HolderId = fennId });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = $"items/{slug}-lantern", Name = "Lantern", Description = "A hooded lantern.", HolderId = fennId });
            if (extra != null)
            {
                await extra(session, slug);
            }
            await session.SaveChangesAsync();
        }

        return new Market(slug, tools, locId, pcId, odaId, fennId, ropeId);
    }

    [Fact]
    public async Task Arrival_SendsSpotlightCardOnce_RosterForTheRest_AndAgainAfterAForcedReseed()
    {
        var m = await SeedMarketAsync();

        var first = await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);
        Assert.True(first.Success, first.Summary);
        Assert.Equal(TurnMode.Full, first.Data!.Mode);
        var odaCard = Assert.Single(first.Data.Cards!, c => c.Id == m.OdaId);
        Assert.Equal("dry humor", odaCard.Traits);
        Assert.Contains("Tamsin: friendly (65)", odaCard.Stance);
        Assert.DoesNotContain(first.Data.Cards!, c => c.Id == m.FennId); // a bystander: roster only

        var fennRow = Assert.Single(first.Data.FullScene!.PresentNPCs, n => n.Id == m.FennId);
        Assert.Null(fennRow.Stats);
        Assert.Empty(fennRow.KnownNeeds);
        Assert.EndsWith("…", fennRow.Notes); // a short hook until the card arrives
        Assert.Equal("Stalls of fish, rope and lamp oil under striped awnings.", first.Data.FullScene.Location.Description);

        var revisit = await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);
        Assert.Equal(TurnMode.Delta, revisit.Data!.Mode);
        Assert.DoesNotContain(revisit.Data.Cards ?? [], c => c.Id == m.OdaId);
        Assert.Equal("(described earlier this session)", revisit.Data.FullScene!.Location.Description);

        var reseeded = await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId, ForceFullReseed = true }, m.Slug);
        Assert.Equal(TurnMode.Full, reseeded.Data!.Mode);
        Assert.Contains(reseeded.Data.Cards!, c => c.Id == m.OdaId);
        Assert.Equal("Stalls of fish, rope and lamp oil under striped awnings.", reseeded.Data.FullScene!.Location.Description);
    }

    [Fact]
    public async Task Bystander_GetsACardOnFirstInvolvement_NotAgain_UntilItChanges()
    {
        var m = await SeedMarketAsync();
        await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);

        Task<ToolResult<TurnResult>> TalkToFenn() => m.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "Tamsin haggles with Fenn.", Category = EventCategory.Conversation, Involved = [m.PcId, m.FennId] }],
            Narrative = "Tamsin haggles with Fenn over a coil of rope."
        }, m.Slug);

        var firstContact = await TalkToFenn();
        Assert.True(firstContact.Success, firstContact.Summary);
        var card = Assert.Single(firstContact.Data!.Cards!, c => c.Id == m.FennId);
        Assert.Equal("gruff", card.Traits);
        Assert.Contains("Rope", card.Gear);

        var again = await TalkToFenn();
        Assert.DoesNotContain(again.Data!.Cards ?? [], c => c.Id == m.FennId);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var fenn = await session.LoadAsync<Character>(m.FennId, TestContext.Current.CancellationToken);
            fenn.Psychology.Traits = ["gruff", "suspicious"];
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var changed = await TalkToFenn();
        Assert.Equal("gruff, suspicious", Assert.Single(changed.Data!.Cards!, c => c.Id == m.FennId).Traits);
    }

    [Fact]
    public async Task TradeBeat_PushesPartyGoldAndTheMerchantsWares()
    {
        var m = await SeedMarketAsync();
        await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);

        var trade = await m.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new ItemTransfer { ItemId = m.RopeId, ToHolderId = m.PcId },
                new ResourceChange { CharacterId = m.PcId, PoolName = "gold", Delta = -2, Reason = "Rope" },
                new EventOccurred { Summary = "Tamsin buys rope from Fenn.", Category = EventCategory.Conversation, Involved = [m.PcId, m.FennId] }
            ],
            Narrative = "Tamsin pays two gold for the rope."
        }, m.Slug);

        Assert.True(trade.Success, trade.Summary);
        Assert.Contains(trade.Data!.Context!, l => l == "Party gold: Tamsin 8");
        Assert.Contains(trade.Data.Context!, l => l.StartsWith("Fenn holds: Lantern"));
    }

    [Fact]
    public async Task StealthCheck_PushesPassivePerceptionOfTheNpcsHere()
    {
        var m = await SeedMarketAsync();
        await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);

        var sneak = await m.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = m.PcId, ActionName = "Sneak past the stalls", ActionType = RulesetActionType.SkillCheck,
                    Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "12" }
                }
            ],
            Narrative = "Tamsin slips between the stalls."
        }, m.Slug);

        Assert.True(sneak.Success, sneak.Summary);
        var line = Assert.Single(sneak.Data!.Context!, l => l.StartsWith("Passive Perception here:"));
        Assert.Contains("Oda 10", line);
        Assert.Contains("Fenn 10", line);
    }

    [Fact]
    public async Task QuestLinkedNpc_IsInTheSpotlight_AndTouchingThemPushesTheObjective()
    {
        var m = await SeedMarketAsync((session, slug) => session.StoreAsync(new Quest
        {
            Id = $"quests/{slug}-oil",
            CampaignName = slug,
            Title = "Who buys the lamp oil?",
            Objectives = [new QuestObjective("Learn who bought the lamp oil", InvolvedIds: [$"chars/{slug}-fenn"])]
        }));

        var arrival = await m.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = m.LocId }, m.Slug);
        Assert.Contains(arrival.Data!.Cards!, c => c.Id == m.FennId); // quest-linked: spotlight

        var ask = await m.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "Tamsin asks Fenn about the oil.", Category = EventCategory.Conversation, Involved = [m.PcId, m.FennId] }],
            Narrative = "Tamsin asks Fenn who has been buying lamp oil."
        }, m.Slug);
        Assert.Contains(ask.Data!.Context!, l => l.Contains("Who buys the lamp oil?") && l.Contains("Learn who bought the lamp oil"));
    }

    [Fact]
    public async Task MemoryRecall_PushesTheMemoryTheNarrativeIsAbout_OncePerSession()
    {
        var slug = "recall-" + Guid.NewGuid().ToString("N")[..8];
        var odaId = $"chars/{slug}-oda";
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Character
        {
            Id = odaId, Name = "Oda", CampaignName = slug,
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["ledger"] = new() { Topic = "The ledger", Details = "Tamsin asked about the missing page yesterday.", SemanticVector = [0.9f, 0.1f, 0f] },
                    ["weather"] = new() { Topic = "Weather", Details = "A storm is coming.", SemanticVector = [0f, 0f, 1f] }
                }
            }
        }, TestContext.Current.CancellationToken);

        var turn = new ContextTurn
        {
            Session = session, CampaignName = slug, Config = new CampaignConfig(),
            AppliedChanges = [], InvolvedEntityIds = [odaId], Party = [], NarrativeVector = [1f, 0f, 0f]
        };
        var orchestrator = new ContextOrchestrator([new MemoryRecallContextContributor()], []);
        var delivered = new List<string>();

        var lines = await orchestrator.CollectAsync(turn, delivered, TestContext.Current.CancellationToken);
        var again = await orchestrator.CollectAsync(turn, delivered, TestContext.Current.CancellationToken);

        Assert.Equal(["Oda recalls — The ledger: Tamsin asked about the missing page yesterday."], lines);
        Assert.Empty(again);
    }

    [Fact]
    public async Task Orchestrator_NamespacesPluginKeys_SkipsDelivered_AndKeepsToTheBudget()
    {
        var turn = new ContextTurn
        {
            Session = null!, CampaignName = "c", Config = new CampaignConfig(),
            AppliedChanges = [], InvolvedEntityIds = [], Party = []
        };
        var big = new string('x', ContextOrchestrator.CharBudget - 10);
        var orchestrator = new ContextOrchestrator(
            [new FixedContributor(new ContextItem("a", big, 9), new ContextItem("b", "second line", 1))],
            [new FixedPlugin(new PluginContextItem("recipe", "Recipe: 2 of 3 herbs gathered.", 5))]);
        var delivered = new List<string>();

        var lines = await orchestrator.CollectAsync(turn, delivered, TestContext.Current.CancellationToken);

        Assert.Equal([big], lines); // the rest would overflow the budget and waits for a later turn
        Assert.Equal(["a"], delivered);

        var next = await orchestrator.CollectAsync(turn, delivered, TestContext.Current.CancellationToken);
        Assert.Equal(["Recipe: 2 of 3 herbs gathered.", "second line"], next);
        Assert.Contains("plugin:CampaignVault.UnitTests:recipe", delivered);
    }

    private sealed class FixedContributor(params ContextItem[] items) : IContextContributor
    {
        public Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<ContextItem>>(items);
    }

    private sealed class FixedPlugin(params PluginContextItem[] items) : IPluginContextContributor
    {
        public Task<IEnumerable<PluginContextItem>> ContributeAsync(IContextTurn turn, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<PluginContextItem>>(items);
    }
}
