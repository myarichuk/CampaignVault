using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Remediation plan phase 4 (M-31 to M-33): unconscious at 0 HP, concentration at 0 HP and through the pipeline, death saves through the pipeline.</summary>
public class MechanicsPhase4Tests
{
    private static Character Pc(int hp = 20, int max = 20) => new()
    {
        Id = "chars/maeve", Name = "Maeve", MaxHp = max, CurrentHp = hp, IsPc = true,
        SystemStats = new Dnd5eExtension(), CurrentLocationId = "locations/gorse",
    };

    private static void Concentrate(Character c) =>
        c.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Concentration: Bless", Category = "Buff" });

    private static ChangeContext Ctx(Character c, RollModifierPipeline? pipeline = null) =>
        ChangeContextTestHelper.Create(
            characters: new() { [c.Id] = c }, summary: [],
            dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance, rollModifiers: pipeline));

    private static IRollService Rolls(int die, List<RollRequest>? seen = null)
    {
        var rolls = Substitute.For<IRollService>();
        rolls.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<RollRequest>();
            seen?.Add(request);
            return Task.FromResult(new RollOutcome { Tag = request.Tag, Result = die + request.Bonus });
        });
        return rolls;
    }

    private static Task<ChangeHandlerResult> Hit(Character c, int delta, IRollService? rolls = null, RollModifierPipeline? pipeline = null) =>
        new HpChangeHandler(rolls ?? Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = c.Id, Delta = delta }, Ctx(c, pipeline), TestContext.Current.CancellationToken);

    private static Task<ChangeHandlerResult> Save(Character c, int die, IRollService? rolls = null, RollModifierPipeline? pipeline = null) =>
        new DeathSaveChangeHandler(rolls ?? Substitute.For<IRollService>())
            .ApplyAsync(new DeathSaveChange { CharacterId = c.Id, Roll = die }, Ctx(c, pipeline), TestContext.Current.CancellationToken);

    private sealed class KindProvider(string kind, int bonus, AdvantageEffect advantage) : IRollModifierProvider
    {
        public IEnumerable<RollModifier> Modifiers(RollQuery query) =>
            query.Kind == kind ? [new RollModifier("test", bonus, advantage, "test provider")] : [];
    }

    private static bool IsDowned(Character c) =>
        c.SystemStats!.StatusEffects.Any(e => e.EffectKey == HitPointState.DownedEffectKey);

    // ---- M-31: unconscious at 0 HP ----

    [Fact]
    public async Task DroppingToZero_FlagsUnconscious_AndActionBlockSeesIt()
    {
        var pc = Pc(hp: 5);

        await Hit(pc, -5);

        Assert.True(IsDowned(pc));
        Assert.True(ActionBlock.IsBlocked(pc, out var blocker));
        Assert.Equal("Unconscious", blocker!.Name);
    }

    [Fact]
    public async Task Healing_RemovesTheEnginesUnconscious_ButNotASleepSpells()
    {
        var pc = Pc(hp: 5);
        pc.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Unconscious", ConditionName = "unconscious", Category = "Condition" });
        await Hit(pc, -5);
        Assert.Equal(2, pc.SystemStats.StatusEffects.Count(e => e.ConditionName == "unconscious"));

        await Hit(pc, 4);

        var left = Assert.Single(pc.SystemStats.StatusEffects, e => e.ConditionName == "unconscious");
        Assert.Null(left.EffectKey);
    }

    [Fact]
    public async Task MassiveDamage_NeverFlagsUnconscious()
    {
        var pc = Pc(hp: 5, max: 10);

        await Hit(pc, -30);

        Assert.True(pc.IsDead);
        Assert.False(IsDowned(pc));
    }

    [Fact]
    public async Task FailedDeathSaves_DieAndDropTheFlag()
    {
        var pc = Pc(hp: 5);
        await Hit(pc, -5);
        pc.DeathSaves = new DeathSaveTally { Failures = 2 };

        await Save(pc, 4);

        Assert.True(pc.IsDead);
        Assert.False(IsDowned(pc));
    }

    [Fact]
    public async Task NaturalTwenty_WakesTheCharacter_AndDropsTheFlag()
    {
        var pc = Pc(hp: 5);
        await Hit(pc, -5);

        await Save(pc, 20);

        Assert.Equal(1, pc.CurrentHp);
        Assert.False(IsDowned(pc));
        Assert.False(ActionBlock.IsBlocked(pc, out _));
    }

    [Fact]
    public async Task Stable_StaysUnconscious()
    {
        var pc = Pc(hp: 5);
        await Hit(pc, -5);
        pc.DeathSaves = new DeathSaveTally { Successes = 2 };

        await Save(pc, 15);

        Assert.True(pc.DeathSaves!.Stable);
        Assert.True(IsDowned(pc));
    }

    [Fact]
    public async Task Revive_LeavesNoFlag()
    {
        var pc = Pc(hp: 5);
        await Hit(pc, -5);
        pc.DeathSaves = new DeathSaveTally { Failures = 2 };
        await Save(pc, 2);
        Assert.True(pc.IsDead);

        await new DeathChangeHandler().ApplyAsync(new DeathChange { CharacterId = pc.Id, Revive = true }, Ctx(pc), TestContext.Current.CancellationToken);

        Assert.False(pc.IsDead);
        Assert.False(IsDowned(pc));
    }

    [Fact]
    public async Task DmOverride_ToZeroAndBack_KeepsTheFlagHonest()
    {
        var pc = Pc(hp: 10);

        HitPointState.Set(pc, 0);
        Assert.True(IsDowned(pc));
        HitPointState.Set(pc, 3);
        Assert.False(IsDowned(pc));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Npc_AtZero_IsNotFlagged()
    {
        var npc = Pc(hp: 4);
        npc.IsPc = false;

        await Hit(npc, -4);

        Assert.False(IsDowned(npc));
    }

    // ---- M-32: concentration ----

    [Fact]
    public async Task Concentration_BreaksAtZeroHp_WithoutARoll()
    {
        var pc = Pc(hp: 5);
        Concentrate(pc);
        var rolls = Substitute.For<IRollService>();

        await Hit(pc, -5, rolls);

        Assert.DoesNotContain(pc.SystemStats!.StatusEffects, e => e.Name.Contains("Concentration"));
        await rolls.DidNotReceive().RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task Concentration_BreaksWhenIncapacitated_WithoutARoll()
    {
        var pc = Pc(hp: 30);
        Concentrate(pc);
        pc.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Stunned", ConditionName = "stunned", Category = "Condition" });
        var rolls = Substitute.For<IRollService>();

        await Hit(pc, -2, rolls);

        Assert.DoesNotContain(pc.SystemStats.StatusEffects, e => e.Name.Contains("Concentration"));
        await rolls.DidNotReceive().RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task Concentration_ProviderForTheNewKind_GrantsAdvantage()
    {
        var pc = Pc(hp: 30);
        Concentrate(pc);
        var seen = new List<RollRequest>();
        var pipeline = new RollModifierPipeline([new KindProvider(RollKinds.ConcentrationSave, 0, AdvantageEffect.Advantage)]);

        await Hit(pc, -4, Rolls(15, seen), pipeline);

        var request = Assert.Single(seen);
        Assert.Equal(DiceMechanic.Advantage, request.Mechanic);
    }

    [Fact]
    public async Task Concentration_ProviderForOrdinarySaves_DoesNotApply()
    {
        var pc = Pc(hp: 30);
        Concentrate(pc);
        var seen = new List<RollRequest>();
        var pipeline = new RollModifierPipeline([new KindProvider("not-a-concentration-save", 0, AdvantageEffect.Advantage)]);

        await Hit(pc, -4, Rolls(15, seen), pipeline);

        Assert.Equal(DiceMechanic.Standard, Assert.Single(seen).Mechanic);
    }

    [Fact]
    public async Task Concentration_StatusSaveBonus_CountsLikeASave()
    {
        var pc = Pc(hp: 30);
        Concentrate(pc);
        pc.SystemStats!.StatusEffects.Add(new StatusEffect
        {
            Name = "Bless", Category = "Buff", StatModifiers = new() { ["AllSaves"] = 3 },
        });
        var seen = new List<RollRequest>();

        await Hit(pc, -4, Rolls(10, seen), new RollModifierPipeline([]));

        Assert.Equal(3, Assert.Single(seen).Bonus);
    }

    // ---- M-33: death saves through the pipeline ----

    [Fact]
    public async Task DeathSave_SaveBonusCounts_TowardTheTotal_NotTheNaturalDie()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();
        pc.SystemStats!.StatusEffects.Add(new StatusEffect
        {
            Name = "Bless", Category = "Buff", StatModifiers = new() { ["AllSaves"] = 2 },
        });

        await Save(pc, 8, pipeline: new RollModifierPipeline([]));

        Assert.Equal(1, pc.DeathSaves!.Successes); // 8 + 2 = 10
        Assert.Equal(0, pc.DeathSaves.Failures);
    }

    [Fact]
    public async Task DeathSave_NaturalOne_IsStillTwoFailures_WithABonus()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();

        await Save(pc, 1, pipeline: new RollModifierPipeline([new KindProvider(RollKinds.DeathSave, 5, AdvantageEffect.None)]));

        Assert.Equal(2, pc.DeathSaves!.Failures);
    }

    [Fact]
    public async Task DeathSave_EngineRolled_AsksTheProviderForAdvantage()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();
        var seen = new List<RollRequest>();
        var handler = new DeathSaveChangeHandler(Rolls(12, seen));

        await handler.ApplyAsync(new DeathSaveChange { CharacterId = pc.Id },
            Ctx(pc, new RollModifierPipeline([new KindProvider(RollKinds.DeathSave, 0, AdvantageEffect.Advantage)])),
            TestContext.Current.CancellationToken);

        Assert.Equal(DiceMechanic.Advantage, Assert.Single(seen).Mechanic);
        Assert.Equal(1, pc.DeathSaves!.Successes);
    }

    // ---- review follow-ups ----

    [Fact]
    public async Task DroppingToZero_AlsoFallsProne_AndProneStaysWhenTheyWake()
    {
        var pc = Pc(hp: 5);

        await Hit(pc, -5);
        Assert.Contains(pc.SystemStats!.StatusEffects, e => e.ConditionName == "prone");

        await Hit(pc, 3);
        Assert.False(IsDowned(pc));
        Assert.Contains(pc.SystemStats.StatusEffects, e => e.ConditionName == "prone");
    }

    [Fact]
    public async Task AlreadyProne_IsNotStackedWithAnotherProne()
    {
        var pc = Pc(hp: 5);
        pc.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Prone", ConditionName = "prone", Category = "Condition" });

        await Hit(pc, -5);

        Assert.Single(pc.SystemStats.StatusEffects, e => e.ConditionName == "prone");
    }

    [Fact]
    public async Task DeathSave_BackfillsTheFlag_ForAPcDownedBeforeTheEngineFlaggedThem()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();
        Assert.False(IsDowned(pc));

        await Save(pc, 12);

        Assert.True(IsDowned(pc));
    }

    [Fact]
    public async Task DeathSave_OnTheDyingTurn_IsSpentAfterOneRoll()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();
        var combatant = new CombatantState
        {
            CharacterId = pc.Id, ActionBudget = new() { [CombatTools.DeathSaveDueKey] = 1 },
        };
        var encounter = new CombatEncounter { Id = "combat", IsActive = true, ActiveTurnId = pc.Id, Combatants = [combatant] };
        var ctx = ChangeContextTestHelper.Create(characters: new() { [pc.Id] = pc }, summary: [], activeCombat: encounter);
        var handler = new DeathSaveChangeHandler(Substitute.For<IRollService>());
        var change = new DeathSaveChange { CharacterId = pc.Id, Roll = 12 };

        var first = await handler.ApplyAsync(change, ctx, TestContext.Current.CancellationToken);
        var second = await handler.ApplyAsync(change, ctx, TestContext.Current.CancellationToken);

        Assert.True(first.Success);
        Assert.Equal(0, combatant.ActionBudget[CombatTools.DeathSaveDueKey]);
        Assert.False(second.Success);
        Assert.Equal(1, pc.DeathSaves!.Successes);
    }

    [Fact]
    public async Task DeathSave_OutsideTheDyingTurn_IsNotLimited()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();

        Assert.True((await Save(pc, 12)).Success);
        Assert.True((await Save(pc, 12)).Success);
        Assert.Equal(2, pc.DeathSaves!.Successes);
    }
}
