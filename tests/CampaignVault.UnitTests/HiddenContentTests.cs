using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// T5c (TAKE_TURN_PLAN.md): secret ways, concealed items, secret details and traps stay off the scene
/// payloads, reach the DM once as a first-visit dmOnly line, and are resolved by the engine: checks and
/// passive Perception reveal, traps fire on their trigger, disarm checks make them safe. Plus T5's
/// reasoned, reported party separation.
/// </summary>
[Collection("RavenDB")]
public class HiddenContentTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public HiddenContentTests(RavenDBFixture fixture) => _fixture = fixture;

    private sealed record Study(string Slug, CampaignTools Tools, string StudyId, string CellarId, string YardId,
        string PcId, string CompanionId, string DeskId, string KeyId, string CoinsId, string LetterId);

    /// <summary>A study with a hidden way to the cellar (DC 30), a desk (with a secret compartment, DC 12)
    /// holding a hidden key (DC 8), a trapped coin box, a trapped way out to the yard, and a PC + companion
    /// standing in the yard (passive Perception 10).</summary>
    private async Task<Study> SeedAsync()
    {
        var slug = "hidden-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var s = new Study(slug, tools, $"locations/{slug}-study", $"locations/{slug}-cellar", $"locations/{slug}-yard",
            $"chars/{slug}-pc", $"chars/{slug}-bram", $"items/{slug}-desk", $"items/{slug}-key", $"items/{slug}-coins",
            $"items/{slug}-letter");

        using var session = _fixture.Store.OpenAsyncSession();
        var cs = _fixture.CreateCampaignSession(session, slug);
        await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = s.CellarId, Name = "Cellar", Description = "Damp stone." });
        await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
        {
            Id = s.YardId, Name = "Yard", Description = "A muddy yard.",
            Exits = [new LocationExit(s.StudyId, "The study door", TravelCostHours: 0.1)]
        });
        await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
        {
            Id = s.StudyId, Name = "Study", Description = "Books, a desk, a cold hearth.",
            Exits =
            [
                new LocationExit(s.CellarId, "Behind the bookcase", TravelCostHours: 0.1, Hidden: true, DiscoverDc: 30,
                    Intent: "The smuggler's way down; a draft moves the candle."),
                new LocationExit(s.YardId, "The study door", TravelCostHours: 0.1,
                    Hazard: new Hazard { Name = "tripwire", Trigger = "enter", DetectDc = 40, DisarmDc = 1, Effect = "a bell rings and a dart flies, 1d4 piercing", SaveDc = 12, SaveAbility = "Dexterity" })
            ]
        });
        await repo.UpsertItemAsync(cs, new ItemUpsertRequest
        {
            Id = s.DeskId, Name = "Desk", Description = "An oak desk.", HolderId = s.StudyId,
            ItemDetails = [new ItemDetailUpsertRequest { Name = "False bottom", Description = "A thin panel under the drawer.", Hidden = true, DiscoverDc = 12, Intent = "Hides the ledger page." }]
        });
        await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = s.KeyId, Name = "Brass key", Description = "Small.", HolderId = s.DeskId, Hidden = true, DiscoverDc = 8 });
        await repo.UpsertItemAsync(cs, new ItemUpsertRequest
        {
            Id = s.CoinsId, Name = "Coin box", Description = "A locked box.", HolderId = s.StudyId,
            Hazard = new Hazard { Name = "needle", Trigger = "take", DetectDc = 40, Effect = "poison needle, 1d4 poison", SaveDc = 11, SaveAbility = "Constitution" }
        });
        await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = s.LetterId, Name = "Letter", Description = "Sealed.", HolderId = s.StudyId });
        await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
        {
            Id = s.PcId, Name = "Tamsin", IsPc = true, CurrentLocationId = s.YardId, MaxHp = 10, CurrentHp = 10,
            SystemStats = new Dnd5eExtension { ArmorClass = 12, Wisdom = 10 }
        });
        await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
        {
            Id = s.CompanionId, Name = "Bram", IsPartyCompanion = true, CurrentLocationId = s.YardId, MaxHp = 10, CurrentHp = 10,
            SystemStats = new Dnd5eExtension { ArmorClass = 14, Wisdom = 10 }
        });
        await session.SaveChangesAsync();
        return s;
    }

    private static Task<ToolResult<TurnResult>> Enter(Study s) => s.Tools.TakeTurn(new TakeTurnRequest
    {
        Changes = [new TravelChange { CharacterId = s.PcId, DestinationLocationId = s.StudyId }],
        Narrative = "Tamsin steps into the study.",
        FullDetailLocationId = s.StudyId
    }, s.Slug);

    private static Task<ToolResult<TurnResult>> Check(Study s, string skill, int dc, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string> { ["skill"] = skill, ["dc"] = dc.ToString() };
        foreach (var (k, v) in extra ?? []) parameters[k] = v;
        return s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new RulesetAction { CharacterId = s.PcId, ActionName = skill, ActionType = RulesetActionType.SkillCheck, Parameters = parameters }],
            Narrative = $"Tamsin tries {skill}."
        }, s.Slug);
    }

    [Fact]
    public async Task Secrets_StayOffTheWire_TheDmHearsThemOnTheFirstVisit_PassivePerceptionNotices()
    {
        var s = await SeedAsync();
        var arrival = await Enter(s);
        Assert.True(arrival.Success, arrival.Summary);
        var scene = arrival.Data!.FullScene!;

        Assert.DoesNotContain(scene.Location.Exits, e => e.TargetLocationId == s.CellarId); // secret way
        var desk = Assert.Single(scene.VisibleItems, i => i.Id == s.DeskId);
        Assert.Null(desk.ItemDetails); // the false bottom's name would give it away
        Assert.Contains(arrival.Data.Summary, l => l.Contains("NOTICED") && l.Contains("Brass key")); // DC 8 vs passive 10
        Assert.DoesNotContain(arrival.Data.Summary, l => l.Contains("False bottom")); // DC 12: not passively

        Assert.NotNull(scene.DmOnly);
        Assert.Contains(scene.DmOnly!, l => l.Contains(s.CellarId) && l.Contains("find DC 30") && l.Contains("smuggler"));
        Assert.Contains(scene.DmOnly!, l => l.StartsWith("Desk: False bottom (secret, find DC 12)"));
        Assert.Contains(scene.DmOnly!, l => l.Contains("trap 'needle'") && l.Contains("fires on take"));

        var revisit = await s.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = s.StudyId }, s.Slug);
        Assert.Null(revisit.Data!.FullScene!.DmOnly); // once per session
    }

    [Fact]
    public async Task AnInvestigationCheck_RevealsWhatItsTotalMeets_AndNothingHarder()
    {
        var s = await SeedAsync();
        await Enter(s);

        var search = await Check(s, "Investigation", 5); // any d20 total clears DC 8/12 at +0? not guaranteed, so assert by rolled total
        Assert.True(search.Success, search.Summary);
        var rolled = int.Parse(System.Text.RegularExpressions.Regex.Match(string.Join(" ", search.Data!.Summary), @"Rolled (-?\d+)").Groups[1].Value);

        var found = string.Join("\n", search.Data.Summary);
        Assert.Equal(rolled >= 12, found.Contains("False bottom"));
        Assert.DoesNotContain(s.CellarId, found); // DC 30 is out of reach of a d20 at +0

        var after = await s.Tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = s.StudyId, ForceFullReseed = true }, s.Slug);
        var desk = Assert.Single(after.Data!.FullScene!.VisibleItems, i => i.Id == s.DeskId);
        Assert.Equal(rolled >= 12, desk.ItemDetails?.Any(d => d.Name == "False bottom") == true);
        Assert.DoesNotContain(after.Data.FullScene.Location.Exits, e => e.TargetLocationId == s.CellarId);
    }

    [Fact]
    public async Task ATrappedExit_GoesOffOnce_AndADisarmCheckMakesAnotherSafe()
    {
        var s = await SeedAsync();
        await Enter(s);

        var leave = await s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new TravelChange { CharacterId = s.PcId, DestinationLocationId = s.YardId }],
            Narrative = "Tamsin heads back out."
        }, s.Slug);
        Assert.True(leave.Success, leave.Summary);
        var hazard = Assert.Single(leave.Data!.Summary, l => l.StartsWith("HAZARD: tripwire"));
        Assert.Contains("SavingThrow (Dexterity, dc 12)", hazard);

        var again = await s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new TravelChange { CharacterId = s.PcId, DestinationLocationId = s.StudyId }],
            Narrative = "Tamsin goes back in."
        }, s.Slug);
        var andOut = await s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new TravelChange { CharacterId = s.PcId, DestinationLocationId = s.YardId }],
            Narrative = "And out again."
        }, s.Slug);
        Assert.True(again.Success && andOut.Success);
        Assert.DoesNotContain(andOut.Data!.Summary, l => l.StartsWith("HAZARD")); // spent

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var yard = await session.LoadAsync<Location>(s.YardId, TestContext.Current.CancellationToken);
            yard.Hazards.Add(new Hazard { Name = "snare", Trigger = "enter", DetectDc = 40, DisarmDc = 1, Effect = "a snare" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var disarm = await Check(s, "Sleight of Hand", 1, new Dictionary<string, string> { ["disarm"] = "snare" });
        Assert.True(disarm.Success, disarm.Summary);
        Assert.Contains(disarm.Data!.Summary, l => l.Contains("DISARMED: Tamsin makes the snare here safe"));
    }

    [Fact]
    public async Task TakingATrappedItem_SetsItOff()
    {
        var s = await SeedAsync();
        await Enter(s);

        var take = await s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new ItemTransfer { ItemId = s.CoinsId, ToHolderId = s.PcId }],
            Narrative = "Tamsin pockets the coin box."
        }, s.Slug);
        Assert.True(take.Success, take.Summary);
        Assert.Contains(take.Data!.Summary, l => l.StartsWith("HAZARD: needle on Coin box goes off on Tamsin"));

        var letter = await s.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new ItemTransfer { ItemId = s.LetterId, ToHolderId = s.PcId }],
            Narrative = "And the letter."
        }, s.Slug);
        Assert.DoesNotContain(letter.Data!.Summary, l => l.StartsWith("HAZARD"));
    }

    [Fact]
    public async Task Separation_NeedsAReasonAndALongLeg_AndIsReportedWithWhereTheyAre()
    {
        var slug = "separate-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var road = $"locations/{slug}-road";
        var marsh = $"locations/{slug}-marsh";
        var inn = $"locations/{slug}-inn";
        var pc = $"chars/{slug}-pc";
        var bram = $"chars/{slug}-bram";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = road, Name = "Road", Description = "A road.",
                Exits = [new LocationExit(marsh, "Across the marsh", TravelCostHours: 3), new LocationExit(inn, "Next door", TravelCostHours: 0.2)]
            });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = marsh, Name = "Marsh", Description = "Reeds.", Type = LocationType.Wilderness, Exits = [new LocationExit(road, "Back", TravelCostHours: 3)] });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = inn, Name = "Inn", Description = "Warm.", Exits = [new LocationExit(road, "Next door", TravelCostHours: 0.2)] });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = pc, Name = "Tamsin", IsPc = true, CurrentLocationId = road, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = bram, Name = "Bram", IsPartyCompanion = true, CurrentLocationId = road, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Go(string to, string? hazard) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new TravelChange { CharacterId = pc, DestinationLocationId = to, Hazard = hazard, EncounterRiskModifier = -50 },
                new TravelChange { CharacterId = bram, DestinationLocationId = to, Hazard = hazard, EncounterRiskModifier = -50 }
            ],
            Narrative = "The party sets out."
        }, slug);

        var roll = TravelChangeHandler.SeparationRoll;
        TravelChangeHandler.SeparationRoll = () => 0.0; // the roll always "hits"; only the reason gate decides
        try
        {
            var shortHop = await Go(inn, "dense fog"); // under an hour: never
            Assert.True(shortHop.Success, shortHop.Summary);
            Assert.DoesNotContain(shortHop.Data!.Summary, l => l.StartsWith("SEPARATED"));
            await Go(road, null);

            var noReason = await Go(marsh, null); // daytime, no stated hazard: never
            Assert.DoesNotContain(noReason.Data!.Summary, l => l.StartsWith("SEPARATED"));
            await Go(road, null);

            var fog = await Go(marsh, "dense fog");
            Assert.True(fog.Success, fog.Summary);
            Assert.Contains(fog.Data!.Summary, l => l == $"SEPARATED: Bram lost the party in the dense fog and is back at Road ({road}). Reunite by traveling there, or by waiting for them.");
        }
        finally
        {
            TravelChangeHandler.SeparationRoll = roll;
        }

        using var check = _fixture.Store.OpenAsyncSession();
        Assert.Equal(marsh, (await check.LoadAsync<Character>(pc, TestContext.Current.CancellationToken)).CurrentLocationId);
        Assert.Equal(road, (await check.LoadAsync<Character>(bram, TestContext.Current.CancellationToken)).CurrentLocationId);
    }
}
