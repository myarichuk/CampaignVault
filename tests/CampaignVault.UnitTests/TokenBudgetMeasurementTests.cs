using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CampaignVault.Tests;

/// <summary>
/// Standing token-budget harness for the take_turn response shape across the three scene types that
/// dominate real play: combat, social, and travel. Each scenario drives a short run of turns through
/// the real tool stack and measures what the model would actually receive — compact camelCase JSON put
/// through McpResponseCleaner — using <see cref="TokenEstimator"/>.
///
/// This replaces a throwaway measurement script that printed chars/4 and asserted nothing. It is a
/// regression gate now, because "the response got three times more expensive" is otherwise invisible:
/// nothing fails, no error appears, play just quietly starts costing more context and hitting
/// compaction sooner. Two things are held:
///
///  1. ABSOLUTE CEILINGS per scenario. Deliberately loose — roughly double the cost measured when they
///     were set — because the job is to catch an order-of-magnitude regression (a full entity graph
///     leaking into a delta, a stripped field coming back), not to force a test edit every time an NPC
///     gains a trait. Tighten them only alongside a real reduction.
///
///  2. The DELTA RATIO, applied to the STATE portion of the response only. This is the assertion that
///     actually protects the design: full/delta alternation is the central token-conservation mechanism
///     in take_turn, and it can be defeated silently by any change that makes delta mode re-send state
///     the client already has. Being a ratio, it stays meaningful as absolute sizes drift with content.
///
///     "State portion" matters. A turn's response is two different things glued together: server-held
///     world state (scenes, party, world state — what delta mode exists to shrink) and a commit echo
///     (`summary`, one confirmation line per change the caller just sent). The echo scales with the
///     CALLER's input, not with anything delta mode controls, so including it makes the ratio measure
///     the wrong thing — a three-change travel commit can out-cost the full snapshot that preceded it
///     purely on echo lines while its state portion behaved perfectly. The first version of this
///     harness gated on the total and failed exactly that way.
///
/// On failure the harness prints a per-property token breakdown of the offending turn, so the next
/// person sees which field grew instead of a 40KB blob.
/// </summary>
[Collection("RavenDB")]
public class TokenBudgetMeasurementTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly ITestOutputHelper _output;

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Ceiling for a single turn's response, per scenario — roughly double the dearest turn measured
    /// when they were set (combat ~1550, social ~1270, travel ~1120 total tokens). Combat is the
    /// dearest scene type: four combatants carrying stats on top of the scene itself.
    /// </summary>
    private const int CombatTurnTokenCeiling = 3200;

    private const int SocialTurnTokenCeiling = 2600;
    private const int TravelTurnTokenCeiling = 2400;

    /// <summary>
    /// A delta turn may return at most this fraction of the STATE the full snapshot before it returned.
    /// Measured share across the three scenarios sits at 33–42%, so 0.65 leaves real room for content to
    /// grow while still failing well before delta mode degenerates into a second full snapshot.
    /// </summary>
    private const double MaxDeltaShareOfFull = 0.65;

    public TokenBudgetMeasurementTests(RavenDBFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private sealed record TurnCost(int TurnNumber, TurnMode Mode, int Tokens, int StateTokens, int Chars, string Json);

    /// <summary>
    /// The response properties that carry server-held world state — the part delta mode is responsible
    /// for shrinking. Everything else is either fixed-size bookkeeping (mode, worldSequence) or a commit
    /// echo whose size the caller chose.
    /// </summary>
    private static readonly string[] StateProperties =
    [
        "npcs", "scenes", "party", "partyDelta", "worldState", "worldStateDelta", "fullNpcContext", "fullScene"
    ];

    /// <summary>Measures one turn, logs it, and returns the cost so the scenario can assert on it.</summary>
    private TurnCost Measure(string scenario, int turnNumber, TurnResult data)
    {
        var (tokens, chars, json) = TokenEstimator.EstimateWireCost(data, WireOptions);

        var breakdown = TokenEstimator.Breakdown(json).ToList();
        var stateTokens = breakdown
            .Where(b => StateProperties.Contains(b.Property, StringComparer.OrdinalIgnoreCase))
            .Sum(b => b.Tokens);

        _output.WriteLine(
            $"[{scenario}] turn {turnNumber} mode={data.Mode} tokens~={tokens} (state ~{stateTokens}) chars={chars}");
        return new TurnCost(turnNumber, data.Mode, tokens, stateTokens, chars, json);
    }

    /// <summary>Applies both gates to a scenario's turns and, on failure, prints where the tokens went.</summary>
    private void AssertWithinBudget(string scenario, int ceiling, IReadOnlyList<TurnCost> turns)
    {
        foreach (var turn in turns)
        {
            if (turn.Tokens > ceiling)
            {
                DumpBreakdown(scenario, turn);
            }

            Assert.True(turn.Tokens <= ceiling,
                $"[{scenario}] turn {turn.TurnNumber} (mode={turn.Mode}) cost ~{turn.Tokens} tokens, over the " +
                $"{ceiling} ceiling. See the per-property breakdown above.");
        }

        // Compare each delta turn against the most recent full snapshot before it. A scenario whose
        // turns are all one mode simply has nothing to compare and skips this gate rather than
        // inventing a baseline.
        TurnCost? lastFull = null;
        foreach (var turn in turns)
        {
            if (turn.Mode == TurnMode.Full)
            {
                lastFull = turn;
                continue;
            }

            if (lastFull is null)
            {
                continue;
            }

            if (lastFull.StateTokens == 0)
            {
                // Nothing to shrink relative to — the full turn returned no state section at all.
                continue;
            }

            var share = (double)turn.StateTokens / lastFull.StateTokens;
            _output.WriteLine(
                $"[{scenario}] turn {turn.TurnNumber} delta state is {share:P0} of the full snapshot at turn {lastFull.TurnNumber}");

            if (share > MaxDeltaShareOfFull)
            {
                DumpBreakdown(scenario, turn);
            }

            Assert.True(share <= MaxDeltaShareOfFull,
                $"[{scenario}] delta turn {turn.TurnNumber} returned {share:P0} as much STATE as the full snapshot at " +
                $"turn {lastFull.TurnNumber} (~{turn.StateTokens} vs ~{lastFull.StateTokens} tokens). Delta mode is " +
                "supposed to send only what changed — something is re-sending state the client already has.");
        }
    }

    private void DumpBreakdown(string scenario, TurnCost turn)
    {
        _output.WriteLine($"[{scenario}] turn {turn.TurnNumber} token breakdown (largest first):");
        foreach (var (property, tokens) in TokenEstimator.Breakdown(turn.Json).Take(12))
        {
            _output.WriteLine($"    {property,-28} ~{tokens}");
        }
    }

    [Fact]
    public async Task CombatScene_StaysWithinTokenBudget()
    {
        var slug = NewSlug("tok-combat");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-ruins";
        var pcId = $"chars/{slug}-pc";
        var companionId = $"chars/{slug}-comp";
        var enemyAId = $"chars/{slug}-enemy-a";
        var enemyBId = $"chars/{slug}-enemy-b";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            { Id = locId, Name = "Collapsed Shrine", Description = "Rubble-strewn floor, one wall open to the night sky." });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "Aria", IsPc = true, CurrentLocationId = locId, MaxHp = 24, CurrentHp = 24 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Bram", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 18, CurrentHp = 18 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = enemyAId, Name = "Cultist", CurrentLocationId = locId, MaxHp = 14, CurrentHp = 14 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = enemyBId, Name = "Cultist Adept", CurrentLocationId = locId, MaxHp = 16, CurrentHp = 16 });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Turn(WorldChange[]? changes, string? narrative) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = narrative,
            ExtraCharacterIds = [pcId, companionId, enemyAId, enemyBId],
            ExtraLocationIds = [locId]
        }, slug);

        var t1 = await Turn(null, null);
        Assert.True(t1.Success, t1.Summary);
        var c1 = Measure("Combat", 1, t1.Data!);

        var t2 = await Turn(
        [
            new HpChange { CharacterId = enemyAId, Delta = -7 },
            new HpChange { CharacterId = pcId, Delta = -5 },
            new StatusChange { CharacterId = enemyAId, Status = "Bleeding" },
            new EventOccurred { Summary = "Aria's blade opens the cultist's side; he staggers back bleeding.", Category = EventCategory.Combat, Involved = [pcId, enemyAId] }
        ], "Steel rings against stone. Aria's strike lands true, the cultist reeling.");
        Assert.True(t2.Success, t2.Summary);
        var c2 = Measure("Combat", 2, t2.Data!);

        var t3 = await Turn(
        [
            new HpChange { CharacterId = enemyAId, Delta = -14 },
            new HpChange { CharacterId = companionId, Delta = -6 },
            new StatusRemove { CharacterId = enemyAId, Status = "Bleeding" },
            new EventOccurred { Summary = "The wounded cultist falls; the adept lashes out at Bram in retaliation.", Category = EventCategory.Combat, Involved = [companionId, enemyAId, enemyBId] }
        ], "The cultist collapses. The adept shrieks and drives a dagger into Bram's shoulder.");
        Assert.True(t3.Success, t3.Summary);
        var c3 = Measure("Combat", 3, t3.Data!);

        AssertWithinBudget("Combat", CombatTurnTokenCeiling, [c1, c2, c3]);
    }

    [Fact]
    public async Task SocialScene_StaysWithinTokenBudget()
    {
        var slug = NewSlug("tok-social");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-tavern";
        var pcId = $"chars/{slug}-pc";
        var barkeepId = $"chars/{slug}-barkeep";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            { Id = locId, Name = "The Rusty Nail", Description = "A smoky tavern near the docks, low light and quiet chatter." });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "Aria", IsPc = true, CurrentLocationId = locId, MaxHp = 24, CurrentHp = 24 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = barkeepId, Name = "Old Tam", CurrentLocationId = locId, MaxHp = 12, CurrentHp = 12,
                CurrentAppearance = "Bald, heavyset, missing two front teeth"
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId, Name = "Bram", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 18, CurrentHp = 18,
                CurrentAppearance = "Scarred forearms, travel-worn cloak"
            });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Turn(WorldChange[]? changes, string? narrative) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = narrative,
            PartyLocationId = locId,
            ExtraLocationIds = [locId]
        }, slug);

        var t1 = await Turn(null, null);
        Assert.True(t1.Success, t1.Summary);
        var s1 = Measure("Social", 1, t1.Data!);

        var t2 = await Turn(
        [
            new MoodChange { CharacterId = barkeepId, NewMood = "relieved" },
            new RelationshipChange { CharacterId = barkeepId, TargetId = pcId, Delta = 8, Reason = "Aria paid off his tab discreetly." },
            new EventOccurred
            {
                Summary = "Aria quietly settles Tam's debt with the dock authority; he softens toward her.",
                Category = EventCategory.Conversation,
                Involved = [pcId, barkeepId]
            }
        ], "Aria leans in and slides a pouch across the bar. Tam's shoulders drop with relief.");
        Assert.True(t2.Success, t2.Summary);
        var s2 = Measure("Social", 2, t2.Data!);

        var t3 = await Turn(
        [
            new EventOccurred
            {
                Summary = "Tam mentions a ship that left port two nights early, cargo manifest unclear.",
                Category = EventCategory.Unresolved,
                Involved = [pcId, barkeepId]
            }
        ], "Tam lowers his voice: 'Funny thing, the Marrow's Gale left two nights early. Manifest never got filed.'");
        Assert.True(t3.Success, t3.Summary);
        var s3 = Measure("Social", 3, t3.Data!);

        AssertWithinBudget("Social", SocialTurnTokenCeiling, [s1, s2, s3]);
    }

    [Fact]
    public async Task TravelScene_StaysWithinTokenBudget()
    {
        var slug = NewSlug("tok-travel");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locAId = $"locations/{slug}-road";
        var locBId = $"locations/{slug}-crossing";
        var locCId = $"locations/{slug}-outpost";
        var pcId = $"chars/{slug}-pc";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locAId, Name = "Coast Road" });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locBId, Name = "River Crossing" });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locCId, Name = "Border Outpost" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "Aria", IsPc = true, CurrentLocationId = locAId, MaxHp = 24, CurrentHp = 24 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Bram", IsPartyCompanion = true, CurrentLocationId = locAId, MaxHp = 18, CurrentHp = 18 });
            await session.SaveChangesAsync();
        }

        var t1 = await tools.TakeTurn(new TakeTurnRequest { PartyLocationId = locAId, ExtraLocationIds = [locAId] }, slug);
        Assert.True(t1.Success, t1.Summary);
        var v1 = Measure("Travel", 1, t1.Data!);

        var t2 = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new ActivityChange { CharacterId = pcId, NewLocationId = locBId, UpdateLocation = true, Reason = "Following the coast road inland." },
                new ActivityChange { CharacterId = companionId, NewLocationId = locBId, UpdateLocation = true, Reason = "Following the coast road inland." },
                new EventOccurred { Summary = "The party reaches the river crossing by midafternoon; the ferry is out.", Category = EventCategory.Arrival, Involved = [pcId, companionId], LocationId = locBId }
            ],
            Narrative = "Two hours on, the road opens onto a wide river crossing. The ferry rope hangs slack — no ferryman in sight.",
            MinutesElapsed = 120,
            ExtraLocationIds = [locBId]
        }, slug);
        Assert.True(t2.Success, t2.Summary);
        var v2 = Measure("Travel", 2, t2.Data!);

        var t3 = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new ActivityChange { CharacterId = pcId, NewLocationId = locCId, UpdateLocation = true, Reason = "Fording the shallows and pressing on to the outpost." },
                new ActivityChange { CharacterId = companionId, NewLocationId = locCId, UpdateLocation = true, Reason = "Fording the shallows and pressing on to the outpost." },
                new EventOccurred { Summary = "The party wades the shallows and reaches the border outpost as dusk falls.", Category = EventCategory.Arrival, Involved = [pcId, companionId], LocationId = locCId }
            ],
            Narrative = "The water is cold but shallow. By the time you reach the far bank, the outpost's watchfires are already lit.",
            MinutesElapsed = 90,
            ExtraLocationIds = [locCId]
        }, slug);
        Assert.True(t3.Success, t3.Summary);
        var v3 = Measure("Travel", 3, t3.Data!);

        AssertWithinBudget("Travel", TravelTurnTokenCeiling, [v1, v2, v3]);
    }
}
