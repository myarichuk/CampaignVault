using System;
using System.Collections.Generic;
using System.Linq;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>M-80: minimum range, long range, a hostile within Touch, and an area's origin range.</summary>
public class RangeValidationTests
{
    private static ChangeContext Context(params Character[] characters)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            Substitute.For<IAsyncDocumentSession>(),
            characters.ToDictionary(c => c.Id),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            null,
            null,
            "camp");
    }

    private static Character Pc(string id) => new() { Id = id, Name = id, IsPc = true, CurrentHp = 10, MaxHp = 10, SystemStats = new Dnd5eExtension() };
    private static Character Foe(string id) => new() { Id = id, Name = id, CurrentHp = 10, MaxHp = 10, SystemStats = new Dnd5eExtension() };

    private static void Place(Character from, string to, string band) =>
        from.SystemStats!.SpatialPositions.Add(new SpatialPosition { TargetId = to, DistanceBand = band });

    private static RulesetAction Attack(string actor, string target, params (string Key, string Value)[] parameters)
    {
        var action = new RulesetAction { CharacterId = actor, TargetIds = [target], ActionType = RulesetActionType.Attack, ActionName = "Shot" };
        foreach (var (key, value) in parameters)
        {
            action.Parameters[key] = value;
        }

        return action;
    }

    [Fact]
    public void LongRange_BetweenNormalAndLong_IsAllowedWithDisadvantage()
    {
        var a = Pc("a"); var t = Foe("t"); Place(a, "t", "Far");
        var outcome = RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Near"), ("longRange", "Far")), Context(a, t));
        Assert.Null(outcome.Error);
        Assert.Contains("long range", outcome.DisadvantageReason);
    }

    [Fact]
    public void LongRange_BeyondTheLongBand_IsRefused()
    {
        var a = Pc("a"); var t = Foe("t"); Place(a, "t", "Distant");
        var outcome = RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Near"), ("longRange", "Far")), Context(a, t));
        Assert.NotNull(outcome.Error);
        Assert.Contains("Far", outcome.Error);
    }

    [Fact]
    public void NoLongRange_BeyondNormal_IsStillRefused()
    {
        var a = Pc("a"); var t = Foe("t"); Place(a, "t", "Far");
        Assert.NotNull(RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Near")), Context(a, t)).Error);
    }

    [Fact]
    public void MinRange_TooClose_IsRefused()
    {
        var a = Pc("a"); var t = Foe("t"); Place(a, "t", "Touch");
        var outcome = RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Far"), ("minRange", "Close")), Context(a, t));
        Assert.Contains("minimum range", outcome.Error);
    }

    [Fact]
    public void RangedAttack_WithAHostileWithinTouch_HasDisadvantage()
    {
        var a = Pc("a"); var t = Foe("t"); var other = Foe("other");
        Place(a, "t", "Near"); Place(other, "a", "Touch");
        var outcome = RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Far"), ("weaponTags", "ranged,two-handed")), Context(a, t, other));
        Assert.Null(outcome.Error);
        Assert.Contains("other", outcome.DisadvantageReason);
    }

    [Fact]
    public void RangedAttack_WithOnlyAnAllyWithinTouch_HasNoDisadvantage()
    {
        var a = Pc("a"); var t = Foe("t"); var ally = Pc("ally");
        Place(a, "t", "Near"); Place(a, "ally", "Touch");
        Assert.Null(RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Far"), ("weaponTags", "ranged")), Context(a, t, ally)).DisadvantageReason);
    }

    [Fact]
    public void MeleeAttack_WithAHostileWithinTouch_HasNoDisadvantage()
    {
        var a = Pc("a"); var t = Foe("t"); Place(a, "t", "Touch");
        Assert.Null(RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Touch"), ("weaponTags", "melee")), Context(a, t)).DisadvantageReason);
    }

    [Fact]
    public void RangedAttack_NoPositionsRecorded_PassesCleanly()
    {
        var a = Pc("a"); var t = Foe("t");
        var outcome = RangeValidationHelper.Evaluate(Attack("a", "t", ("range", "Near"), ("weaponTags", "ranged")), Context(a, t));
        Assert.Equal(default, outcome);
    }

    [Fact]
    public void Area_OriginBeyondSpellRange_IsRefusedEvenWhenTheTargetIsNearTheOrigin()
    {
        var a = Pc("a"); var t = Foe("t"); var point = Foe("point");
        Place(a, "point", "Distant"); Place(point, "t", "Touch");
        var action = new RulesetAction { CharacterId = "a", TargetIds = ["t"], ActionType = RulesetActionType.Spell, ActionName = "Fireball" };
        action.Parameters["range"] = "Far"; action.Parameters["aoeRadius"] = "Close"; action.Parameters["originId"] = "point";
        Assert.Contains("origin", RangeValidationHelper.Evaluate(action, Context(a, t, point)).Error);
    }

    [Fact]
    public void Area_OriginInRange_ChecksTheRadiusFromTheOrigin()
    {
        var a = Pc("a"); var t = Foe("t"); var point = Foe("point");
        Place(a, "point", "Far"); Place(point, "t", "Near");
        var action = new RulesetAction { CharacterId = "a", TargetIds = ["t"], ActionType = RulesetActionType.Spell, ActionName = "Fireball" };
        action.Parameters["range"] = "Far"; action.Parameters["aoeRadius"] = "Close"; action.Parameters["originId"] = "point";
        Assert.Contains("out of Close range", RangeValidationHelper.Evaluate(action, Context(a, t, point)).Error);
    }
}
