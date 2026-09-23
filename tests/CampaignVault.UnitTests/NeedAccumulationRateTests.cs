using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 4 tests for NEEDS_ACCUMULATION_RATE_PLAN.md: per-need passive accumulation rates.
///
/// NOTE (known quirk, not fixed in this pass): both sweep sites gate with
/// `effective > 0.0001f`, so negative rates are skipped rather than reducing the need.
/// All tests below use positive rates only.
/// </summary>
public class NeedAccumulationRateTests
{
    // ── NeedAccumulationMath ──────────────────────────────────────────────

    [Fact]
    public void ComputeDeltas_NoRates_MatchesLegacyFourKeyDict()
    {
        var config = new CampaignConfig();
        var deltas = NeedAccumulationMath.ComputeDeltas(config, days: 1);

        Assert.Equal(4, deltas.Count);
        Assert.Equal(10f, deltas["hunger"], precision: 2);
        Assert.Equal(12f, deltas["thirst"], precision: 2); // 10 * 1.2
        Assert.Equal(8f, deltas["tiredness"], precision: 2); // 10 * 0.8
        Assert.Equal(1.5f, deltas["social_drive"], precision: 2); // 10 * 0.15
    }

    [Fact]
    public void ComputeDeltas_EmptyRatesDict_MatchesNoRates()
    {
        var config = new CampaignConfig();
        var withEmpty = NeedAccumulationMath.ComputeDeltas(config, days: 2, new Dictionary<string, float>());
        var without = NeedAccumulationMath.ComputeDeltas(config, days: 2);

        Assert.Equal(without.Count, withEmpty.Count);
        foreach (var (need, delta) in without)
        {
            Assert.Equal(delta, withEmpty[need], precision: 2);
        }
    }

    [Fact]
    public void ComputeDeltas_NonCoreRate_AppearsAtRateTimesDays()
    {
        var config = new CampaignConfig();
        var rates = new Dictionary<string, float> { ["bladder"] = 6f };

        var deltas = NeedAccumulationMath.ComputeDeltas(config, days: 2, rates);

        Assert.Equal(12f, deltas["bladder"], precision: 2); // 6 * 2
        // Core keys untouched.
        Assert.Equal(20f, deltas["hunger"], precision: 2);
        Assert.Equal(24f, deltas["thirst"], precision: 2);
    }

    [Fact]
    public void ComputeDeltas_CoreNeedRate_OverridesConfigDrivenValue()
    {
        var config = new CampaignConfig { NeedAccumulationRate = 10f };
        var rates = new Dictionary<string, float> { ["hunger"] = 3f };

        var deltas = NeedAccumulationMath.ComputeDeltas(config, days: 2, rates);

        Assert.Equal(6f, deltas["hunger"], precision: 2); // 3 * 2, not 10 * 2
        Assert.Equal(24f, deltas["thirst"], precision: 2); // sibling core key unaffected
    }

    [Fact]
    public void ComputeDeltas_RatesDoNotLeakAcrossCalls()
    {
        // The override applies to that character only — a sibling call without rates
        // must still see the config-driven value (ComputeDeltas is stateless).
        var config = new CampaignConfig();
        var rates = new Dictionary<string, float> { ["hunger"] = 3f };

        var rated = NeedAccumulationMath.ComputeDeltas(config, days: 1, rates);
        var unrated = NeedAccumulationMath.ComputeDeltas(config, days: 1);

        Assert.Equal(3f, rated["hunger"], precision: 2);
        Assert.Equal(10f, unrated["hunger"], precision: 2);
    }

    // ── NeedsAccumulationRule (day-tick sweep) ────────────────────────────

    private static Character MakeNpc(string id, Dictionary<string, float>? accumulationRates = null)
    {
        var npc = new Character { Id = id, Name = id };
        if (accumulationRates is not null)
        {
            npc.Needs.AccumulationRates = accumulationRates;
        }
        return npc;
    }

    private static Task<RuleResult> RunRule(IReadOnlyList<Character> npcs, double daysPassed)
    {
        var rule = new NeedsAccumulationRule();
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 10 },
            [],
            npcs,
            null!,
            daysPassed,
            "test_campaign",
            Config: new CampaignConfig());
        return rule.ApplyAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task NeedsAccumulationRule_OnlyRatedNpcDriftsCustomNeed()
    {
        var rated = MakeNpc("chars/rated", new Dictionary<string, float> { ["bladder"] = 5f });
        var unrated = MakeNpc("chars/unrated");

        var result = await RunRule([rated, unrated], daysPassed: 1);

        var needDeltas = result.Deltas.OfType<NeedChange>().ToList();
        var ratedBladder = needDeltas.SingleOrDefault(d => d.CharacterId == rated.Id && d.Need == "bladder");
        Assert.NotNull(ratedBladder);
        Assert.Equal(5f, ratedBladder.Delta, precision: 2); // 5/day * 1 day

        Assert.DoesNotContain(needDeltas, d => d.CharacterId == unrated.Id && d.Need == "bladder");

        // Core drift still applies to both.
        Assert.Contains(needDeltas, d => d.CharacterId == rated.Id && d.Need == "hunger");
        Assert.Contains(needDeltas, d => d.CharacterId == unrated.Id && d.Need == "hunger");
    }

    [Fact]
    public async Task NeedsAccumulationRule_ZeroHungerRate_NoRavenousMoodOrMoraleDrift()
    {
        // Mood/morale projection must use the same per-character deltas as the NeedChanges:
        // a hunger rate of 0 stops passive drift, so near-threshold hunger can't tip into Ravenous.
        var npc = MakeNpc("chars/fasting", new Dictionary<string, float> { ["hunger"] = 0f, ["tiredness"] = 0f });
        // 64: below both the morale-drift (65) and Ravenous (70) lines; the default +10/day would cross both.
        npc.Needs.ActiveNeeds["hunger"] = NpcMoodThresholds.MoraleDriftHunger - 1f;
        npc.Needs.ActiveNeeds["tiredness"] = 0f;
        npc.Psychology.CurrentMood = "Content";

        var result = await RunRule([npc], daysPassed: 1);

        Assert.DoesNotContain(result.Deltas.OfType<NeedChange>(), d => d.Need == "hunger");
        Assert.DoesNotContain(result.Deltas.OfType<MoodChange>(), m => m.NewMood == "Ravenous");
        Assert.DoesNotContain(result.Deltas.OfType<AttributeChange>(), a => a.Attribute == "morale");
    }

    // ── WorldChangeDispatcher.ApplyMicroTimeNudgeAsync (sub-hour path) ────

    private sealed class TestHandler : IWorldChangeHandler
    {
        private readonly Func<WorldChange, bool> _shouldHandle;
        private readonly Func<WorldChange, IChangeContext, Task<ChangeHandlerResult>> _apply;

        public TestHandler(string name, Func<WorldChange, bool> shouldHandle,
            Func<WorldChange, IChangeContext, Task<ChangeHandlerResult>> apply)
        {
            _shouldHandle = shouldHandle;
            _apply = apply;
        }

        public bool ShouldHandle(WorldChange change) => _shouldHandle(change);

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
            CancellationToken ct = default)
            => _apply(change, context);

        public bool ExtractInvolvedEntities(
            WorldChange change,
            HashSet<string>? characterIds = null,
            HashSet<string>? locationIds = null,
            HashSet<string>? factionIds = null,
            HashSet<string>? questIds = null,
            HashSet<string>? itemIds = null,
            HashSet<string>? allInvolvedIds = null)
        {
            if (!_shouldHandle(change)) return false;
            if (change is HpChange hp)
            {
                characterIds?.Add(hp.CharacterId);
                allInvolvedIds?.Add(hp.CharacterId);
            }
            return true;
        }
    }

    [Fact]
    public async Task Dispatcher_SubHourNudge_HonorsPerCharacterRate()
    {
        // Mirrors Dispatcher_MinutesElapsed_NudgesNeedsForInvolvedCharacters, but with two
        // on-screen characters where only one carries a custom bladder rate.
        var rated = new Character
        {
            Id = "chars/pc1",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 0f } }
        };
        rated.Needs.AccumulationRates["bladder"] = 48f; // 48/day * (30/1440) day = exactly 1.0
        var unrated = new Character
        {
            Id = "chars/pc2",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 0f } }
        };

        var hpHandler = new TestHandler("Hp", c => c is HpChange, (c, ctx) => Task.FromResult(ChangeHandlerResult.Ok));
        var dispatcher = new WorldChangeDispatcher(
            [hpHandler, new NeedChangeHandler()],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var mockSession = Substitute.For<IAsyncDocumentSession>();
        mockSession.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Character> { ["chars/pc1"] = rated, ["chars/pc2"] = unrated });
        mockSession.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Item>());
        mockSession.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Location>());

        // MinutesElapsed on ONE change only: the sum (30) stays sub-hour so the instant
        // nudge runs, while both characters count as on-screen via their CharacterIds.
        var result = await dispatcher.DispatchAsync(
            mockSession,
            [
                new HpChange { CharacterId = "chars/pc1", Delta = 0, MinutesElapsed = 30 },
                new HpChange { CharacterId = "chars/pc2", Delta = 0 }
            ],
            "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Equal(1f, rated.Needs!.ActiveNeeds["bladder"], precision: 2);
        Assert.False(unrated.Needs!.ActiveNeeds.ContainsKey("bladder"));
        // Core nudge still applies to both on-screen characters.
        Assert.Equal(10f * 30 / 1440, rated.Needs.ActiveNeeds["hunger"], precision: 2);
        Assert.Equal(10f * 30 / 1440, unrated.Needs.ActiveNeeds["hunger"], precision: 2);
    }

    // ── NeedChangeHandler (accumulationRate verb) ──────────────────────────

    [Fact]
    public async Task NeedChangeHandler_RateOnlyCommit_SetsRateWithoutPushingNeedValue()
    {
        // Use an already-tracked need ("hunger", seeded at 25): with Delta defaulting to 0
        // the immediate push is a no-op, so ActiveNeeds must come out value-identical.
        var character = new Character { Id = "chars/npc", Name = "Npc" };
        var before = new Dictionary<string, float>(character.Needs.ActiveNeeds);
        var ctx = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character });
        var handler = new NeedChangeHandler();

        var result = await handler.ApplyAsync(
            new NeedChange { CharacterId = character.Id, Need = "hunger", AccumulationRate = 48f }, ctx);

        Assert.True(result.Success);
        Assert.Equal(48f, character.Needs.AccumulationRates["hunger"]);
        // No immediate push: every pre-existing need value is unchanged.
        foreach (var (need, value) in before)
        {
            Assert.Equal(value, character.Needs.ActiveNeeds[need]);
        }
        Assert.Equal(before.Count, character.Needs.ActiveNeeds.Count);
    }

    [Fact]
    public async Task NeedChangeHandler_DeltaPlusRateCommit_DoesBoth()
    {
        var character = new Character { Id = "chars/npc", Name = "Npc" };
        var ctx = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character });
        var handler = new NeedChangeHandler();

        var result = await handler.ApplyAsync(
            new NeedChange { CharacterId = character.Id, Need = "paranoia", Delta = 20f, AccumulationRate = 10f }, ctx);

        Assert.True(result.Success);
        Assert.Equal(20f, character.Needs.ActiveNeeds["paranoia"]);
        Assert.Equal(10f, character.Needs.AccumulationRates["paranoia"]);
    }
}
