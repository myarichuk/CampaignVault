using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Models.Converters;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Regression tests for the full-pass review remediation (clock carry, id/key normalization, parsing).</summary>
public class ReviewRemediationTests
{
    // ── CampaignTime: fractional hours are carried, not rounded away per call ──────────────────

    [Fact]
    public void AdvanceHours_CarriesFractionAcrossCalls_InsteadOfRoundingEachCall()
    {
        var time = new CampaignTime { Hour = 6 };

        for (var i = 0; i < 4; i++)
        {
            time.AdvanceHours(1.4);
        }

        // 5.6 elapsed: the clock face shows 5 whole hours, 0.6 is still pending (rounding each leg alone gave 4).
        Assert.Equal(11, time.Hour);
        Assert.Equal(0.6, time.HourCarry, precision: 9);
        Assert.Equal(5.6, time.UnsimulatedHours, precision: 9);
    }

    [Fact]
    public void AdvanceHours_SubHourCallsEventuallyMoveTheClock()
    {
        var time = new CampaignTime { Hour = 23, Day = 30, Month = 12, Year = 1492 };

        time.AdvanceHours(0.5);
        Assert.Equal(23, time.Hour);
        time.AdvanceHours(0.5);

        // Two half hours cross midnight: day, month and year all roll on the 12x30 calendar.
        Assert.Equal(0, time.Hour);
        Assert.Equal(1, time.Day);
        Assert.Equal(1, time.Month);
        Assert.Equal(1493, time.Year);
        Assert.Equal(1, time.TotalDaysElapsed);
        Assert.Equal(0, time.HourCarry, precision: 9);
    }

    [Fact]
    public void AdvanceHours_WholeHoursAreUnaffectedByTheCarry()
    {
        var time = new CampaignTime { Hour = 6 };
        time.AdvanceHours(8);
        Assert.Equal(14, time.Hour);
        Assert.Equal(0, time.HourCarry);
        time.AdvanceHours(double.NaN);
        time.AdvanceHours(-3);
        Assert.Equal(14, time.Hour);
    }

    // ── DefaultRollService: lenient expression parsing with sane bounds ───────────────────────

    [Theory]
    [InlineData("d20", 1, 20, 0)]
    [InlineData("1d20 + 5", 1, 20, 5)]
    [InlineData(" 2d6 -1 ", 2, 6, -1)]
    [InlineData("3", 1, 0, 3)]
    [InlineData("D8+2", 1, 8, 2)]
    public void ParseExpression_AcceptsBareDieAndWhitespace(string expression, int count, int sides, int flat)
    {
        var (c, s, f) = DefaultRollService.ParseExpression(expression);
        Assert.Equal((count, sides, flat), (c, s, f));
    }

    [Theory]
    [InlineData("100000d6")]
    [InlineData("1d99999")]
    [InlineData("")]
    [InlineData("2d6+1d4")]
    public void ParseExpression_RejectsOutOfRangeAndUnsupportedShapes(string expression)
    {
        Assert.Throws<ArgumentException>(() => DefaultRollService.ParseExpression(expression));
    }

    [Fact]
    public async Task RollAsync_BareD20_RollsOneDie()
    {
        var service = new DefaultRollService(new Random(7));
        var outcome = await service.RollAsync(new RollRequest { Tag = "t", Expression = "d20", Bonus = 2 }, TestContext.Current.CancellationToken);
        Assert.Single(outcome.IndividualDice);
        Assert.InRange(outcome.Result, 3, 22);
    }

    // ── WorldChangeNormalizer: inferred $types must bind under strict member handling ──────────

    [Theory]
    [InlineData("""[{"characterId":"chars/a","delta":-5}]""", typeof(HpChange))]
    [InlineData("""[{"characterId":"chars/a","need":"hunger","delta":5}]""", typeof(NeedChange))]
    [InlineData("""[{"characterId":"chars/a","poolName":"spell_slots_1","delta":-1}]""", typeof(ResourceChange))]
    [InlineData("""[{"characterId":"chars/a","newLocationId":"locations/x","updateLocation":true}]""", typeof(ActivityChange))]
    [InlineData("""[{"characterId":"chars/a","destinationLocationId":"locations/x"}]""", typeof(TravelChange))]
    public void NormalizeChangesArray_InferredTypeDeserializesWithoutUnknownMembers(string json, Type expected)
    {
        var normalized = WorldChangeNormalizer.NormalizeChangesArray(json);
        var parsed = JsonSerializer.Deserialize<WorldChange[]>(normalized, CommitChangesParser.CreateOptions());

        Assert.NotNull(parsed);
        Assert.IsType(expected, Assert.Single(parsed!));
    }

    // ── Case-insensitive key resolution for LLM-authored names ───────────────────────────────

    [Fact]
    public void ResolveNeedKey_MatchesExistingKeyCaseInsensitively_ElseLowerCases()
    {
        var needs = new NeedsProfile();
        needs.ActiveNeeds["hunger"] = 10f;
        needs.AccumulationRates["Wanderlust"] = 2f;

        Assert.Equal("hunger", NeedChangeHandler.ResolveNeedKey(needs, "Hunger"));
        Assert.Equal("hunger", NeedChangeHandler.ResolveNeedKey(needs, " HUNGER "));
        Assert.Equal("Wanderlust", NeedChangeHandler.ResolveNeedKey(needs, "wanderlust"));
        Assert.Equal("paranoia", NeedChangeHandler.ResolveNeedKey(needs, "Paranoia"));
    }

    [Fact]
    public void ResolvePoolKey_MatchesStoredKeyCaseInsensitively()
    {
        var pools = new Dictionary<string, ResourcePool>
        {
            ["spell_slots_1"] = new() { Current = 2, Max = 2 },
            ["hit_dice"] = new() { Current = 1, Max = 1 },
        };

        Assert.Equal("spell_slots_1", ResourceChangeHandler.ResolvePoolKey(pools, "Spell_Slots_1"));
        Assert.Equal("hit_dice", ResourceChangeHandler.ResolvePoolKey(pools, " hit_dice "));
        Assert.Null(ResourceChangeHandler.ResolvePoolKey(pools, "ki_points"));
        Assert.Null(ResourceChangeHandler.ResolvePoolKey(null, "hit_dice"));
    }
}
