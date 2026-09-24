using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// T7 (TAKE_TURN_PLAN.md): per-response wire budgets for take_turn with realistic NPCs (traits, wants,
/// fears, notes, memories, gear, a pressing need), like ToolListBudgetTests does for the tool list.
/// Scratch NPCs with empty psychology make every card look free; these don't. Measured on the cleaned
/// wire JSON (McpResponseCleaner), which is what the model actually reads.
/// </summary>
[Collection("RavenDB")]
public class ResponseBudgetTests : IClassFixture<RavenDBFixture>
{
    // Measured when set (chars): arrival 2806 (two rich spotlight cards ~1.4k of it, roster + location ~1.3k),
    // first-contact beat 1544 (the card ~700 plus two follow-up hints that go out once per session), repeat
    // beat 344, after rest 1701 (one-time rest guidance and hints). The plan's 2.5k arrival target assumed
    // ~120-char synthetic cards; realistic cards are ~700. Ceilings leave ~15-25% headroom.
    private const int ArrivalCeiling = 3300;
    private const int FirstContactBeatCeiling = 1900;
    private const int RepeatBeatCeiling = 450;
    private const int AfterRestCeiling = 2000;

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly RavenDBFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ResponseBudgetTests(RavenDBFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private int Measure(string label, TurnResult data)
    {
        var (_, chars, json) = TokenEstimator.EstimateWireCost(data, WireOptions);
        _output.WriteLine($"[{label}] {chars} chars: {json}");
        return chars;
    }

    [Fact]
    public async Task ArrivalBeatsAndRest_WithRealisticNpcs_StayWithinBudget()
    {
        var slug = "budget-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var street = $"locations/{slug}-street";
        var tavern = $"locations/{slug}-tavern";
        var pc = $"chars/{slug}-tamsin";
        string Npc(string n) => $"chars/{slug}-{n}";
        var names = new[] { ("marta", "Marta the Innkeeper"), ("rhel", "Captain Rhel"), ("jory", "Old Jory"), ("ilsa", "Ilsa the Lutist") };

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = street, Name = "Net Street", Description = "Drying nets and fish smoke.",
                Exits = [new LocationExit(tavern, "The tavern door", TravelCostHours: 0.1)]
            });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = tavern, Name = "The Drowned Bell", Description = "A low-beamed tavern: a peat fire, a ship's bell over the bar, " +
                    "fishermen at long tables, the smell of chowder and wet wool.",
                Exits = [new LocationExit(street, "Back to Net Street", TravelCostHours: 0.1)]
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = pc, Name = "Tamsin", IsPc = true, CurrentLocationId = street, MaxHp = 21, CurrentHp = 21,
                SystemStats = new Dnd5eExtension { ArmorClass = 14, Level = 3 }
            });

            var i = 0;
            foreach (var (key, name) in names)
            {
                var memories = Enumerable.Range(1, 3).ToDictionary(
                    m => $"m{m}",
                    m => new MemoryNode { Topic = $"Harbor talk {m}", Details = $"{name} heard at the harbor that the lamp oil went missing on night {m}, and did not like who asked." });
                await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
                {
                    Id = Npc(key), Name = name, CurrentLocationId = tavern, MaxHp = 11, CurrentHp = 11,
                    CurrentActivity = "Working the room", CurrentAppearance = "Salt-stained apron, sleeves rolled, a quick smile that doesn't reach the eyes.",
                    Notes = $"{name} has kept to this coast for twenty years; knows every captain by name and owes the chapel a favor " +
                            "nobody talks about. Quietly afraid the dark lighthouse means smugglers are back.",
                    Psychology = new PsychologyProfile
                    {
                        Traits = ["patient", "dry humor", "keeps promises"], Wants = ["a quiet season", "the lamp relit"],
                        Fears = ["the sea at night", "debt"], CurrentMood = "wary", Memories = memories
                    },
                    // Two of the four have an opinion of the PC: they are in the spotlight on arrival.
                    Social = i < 2 ? new SocialProfile { Relationships = new Dictionary<string, int> { [pc] = 20 } } : null,
                    Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 30, ["tiredness"] = 65 } },
                    SystemStats = new Dnd5eExtension { ArmorClass = 11, Level = 2 }
                });
                await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = $"items/{slug}-{key}-knife", Name = "Knife", Description = "A plain knife.", HolderId = Npc(key), IsEquipped = true });
                await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = $"items/{slug}-{key}-purse", Name = "Purse", Description = "A few coins.", HolderId = Npc(key) });
                i++;
            }

            await session.SaveChangesAsync();
        }

        var start = await tools.TakeTurn(new TakeTurnRequest { IncludeParty = true }, slug);
        Assert.True(start.Success, start.Summary);
        var fingerprint = start.Data!.PartyFingerprint;

        var arrival = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new TravelChange { CharacterId = pc, DestinationLocationId = tavern }],
            Narrative = "Tamsin ducks in out of the wind.",
            FullDetailLocationId = tavern,
            ClientPartyFingerprint = fingerprint
        }, slug);
        Assert.True(arrival.Success, arrival.Summary);
        fingerprint = arrival.Data!.PartyFingerprint ?? fingerprint;
        Assert.Equal(4, arrival.Data.FullScene!.PresentNPCs.Count());
        Assert.Equal(2, arrival.Data.Cards!.Count); // the two with an opinion of Tamsin

        Task<ToolResult<TurnResult>> TalkTo(string npc, string line) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new EventOccurred { Summary = line, Category = EventCategory.Conversation, Involved = [pc, npc] },
                new MoodChange { CharacterId = npc, NewMood = "guarded" }
            ],
            Narrative = line,
            ClientPartyFingerprint = fingerprint
        }, slug);

        var firstContact = await TalkTo(Npc("jory"), "Tamsin asks Old Jory about the lamp oil.");
        Assert.True(firstContact.Success, firstContact.Summary);
        Assert.Contains(firstContact.Data!.Cards!, c => c.Id == Npc("jory"));
        var repeat = await TalkTo(Npc("jory"), "Tamsin presses Old Jory about the night boats.");
        Assert.True(repeat.Success, repeat.Summary);

        var rest = await tools.AdvanceWorld(1, 7, "Tamsin sleeps at the inn.", slug);
        Assert.True(rest.Success, rest.Summary);
        var afterRest = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "Tamsin wakes to gulls.", Category = EventCategory.Discovery, Involved = [pc] }],
            Narrative = "Morning comes grey.",
            ClientPartyFingerprint = rest.Data!.PartyFingerprint ?? fingerprint
        }, slug);
        Assert.True(afterRest.Success, afterRest.Summary);

        var sizes = new[]
        {
            (Label: "arrival", Chars: Measure("arrival", arrival.Data), Ceiling: ArrivalCeiling),
            (Label: "first-contact beat", Chars: Measure("first-contact beat", firstContact.Data!), Ceiling: FirstContactBeatCeiling),
            (Label: "repeat beat", Chars: Measure("repeat beat", repeat.Data!), Ceiling: RepeatBeatCeiling),
            (Label: "after rest", Chars: Measure("after rest", afterRest.Data!), Ceiling: AfterRestCeiling)
        };

        foreach (var (label, chars, ceiling) in sizes)
        {
            Assert.True(chars <= ceiling, $"take_turn {label}: {chars} chars > {ceiling} budget (see test output for the JSON).");
        }
    }
}
