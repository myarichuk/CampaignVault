using System;
using System.Collections.Generic;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class TurnCadenceAdvisorTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static string NewCampaign() => "cadence-" + Guid.NewGuid().ToString("N")[..8];

    private static WorldChange Roll() => new RulesetAction { ActionName = "Stealth", ActionType = RulesetActionType.SkillCheck, CharacterId = "characters/a" };
    private static WorldChange Knowledge() => new KnowledgeUpdate { CharacterId = "characters/a", Topic = "t", Details = "d" };
    private static WorldChange Activity() => new ActivityChange { CharacterId = "characters/b" };
    private static WorldChange Nudge() => new NpcInitiativeNudge { CharacterId = "characters/b" };

    [Fact]
    public void FirstCommit_NeverWarns() =>
        Assert.Null(TurnCadenceAdvisor.Evaluate(NewCampaign(), [Activity()], T0));

    [Fact]
    public void RollThenKnowledgeUpdate_IsTheApprovedSplit()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Roll()], [], T0);
        Assert.Null(TurnCadenceAdvisor.Evaluate(c, [Knowledge()], T0.AddSeconds(3)));
    }

    [Fact]
    public void RollThenHousekeeping_Warns()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Roll()], [], T0);
        var w = TurnCadenceAdvisor.Evaluate(c, [Knowledge(), Activity(), Nudge()], T0.AddSeconds(3));
        Assert.NotNull(w);
        Assert.Contains("ActivityChange", w);
    }

    [Fact]
    public void HazardSave_IsAllowed_AndFollowUpAfterItIsChecked()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Roll()], ["HAZARD: Trip-line goes off. Resolve now"], T0);
        Assert.Null(TurnCadenceAdvisor.Evaluate(c, [Roll()], T0.AddSeconds(2)));
        TurnCadenceAdvisor.Record(c, [Roll()], [], T0.AddSeconds(2));
        Assert.Null(TurnCadenceAdvisor.Evaluate(c, [Knowledge()], T0.AddSeconds(4)));
    }

    [Fact]
    public void SecondFollowUp_Warns()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Roll()], [], T0);
        TurnCadenceAdvisor.Record(c, [Knowledge()], [], T0.AddSeconds(2));
        Assert.NotNull(TurnCadenceAdvisor.Evaluate(c, [Knowledge()], T0.AddSeconds(4)));
    }

    [Fact]
    public void BackToBackNoRollCommits_Warn()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Activity()], [], T0);
        Assert.NotNull(TurnCadenceAdvisor.Evaluate(c, [Knowledge()], T0.AddSeconds(3)));
    }

    [Fact]
    public void CommitAfterTheWindow_IsANewPlayerBeat()
    {
        var c = NewCampaign();
        TurnCadenceAdvisor.Record(c, [Activity()], [], T0);
        Assert.Null(TurnCadenceAdvisor.Evaluate(c, [Activity()], T0 + TurnCadenceAdvisor.Window + TimeSpan.FromSeconds(1)));
    }
}
