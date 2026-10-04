using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Phase 6 of the mechanics remediation: PF2e correctness (M-50 spell attack, M-51 MAP, M-52 dying and wounded, M-53 mitigation).</summary>
public class MechanicsPhase6Tests
{
    private static ChangeContext Context(params Character[] characters) => new(
        sessionForTests: null,
        characters: characters.ToDictionary(c => c.Id),
        items: new Dictionary<string, Item>(),
        locations: new Dictionary<string, Location>(),
        factions: new Dictionary<string, Faction>(),
        quests: new Dictionary<string, Quest>(),
        logger: NullLogger.Instance,
        summary: [],
        dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);

    private static FakeRollService Rolls(params int[] results)
    {
        var rolls = new FakeRollService();
        foreach (var r in results)
        {
            rolls.NextRolls.Enqueue(new RollOutcome { Result = r, Summary = r.ToString() });
        }

        return rolls;
    }

    private static Character Caster(Action<Pf2eExtension>? tweak = null)
    {
        var stats = new Pf2eExtension { Level = 5, StrengthMod = 4, DexterityMod = 2, IntelligenceMod = 4, SpellcastingAbility = "Intelligence" };
        tweak?.Invoke(stats);
        return new Character { Id = "caster", Name = "Mira", ClassLevel = "Wizard 5", IsPc = true, MaxHp = 40, CurrentHp = 40, SystemStats = stats };
    }

    private static Character Dummy(int ac = 10, Action<Pf2eExtension>? tweak = null)
    {
        var stats = new Pf2eExtension { ArmorClass = ac };
        tweak?.Invoke(stats);
        return new Character { Id = "dummy", Name = "Dummy", MaxHp = 50, CurrentHp = 50, SystemStats = stats };
    }

    private static RulesetAction SpellAttack(params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "caster", TargetIds = ["dummy"], ActionType = RulesetActionType.Spell, ActionName = "Ray of Frost",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d4" },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    // ---- M-50: spell attack ----

    [Fact]
    public async Task M50_TheSpellcastingStep_DerivesTheAttackAsTheDcMinusTen()
    {
        var caster = Caster();
        var context = new BootstrapContext { Character = caster, ActiveSystem = RulesetSystem.Pathfinder2e };

        await new Pf2eDeriveSpellcastingStep().ApplyAsync(context, TestContext.Current.CancellationToken);

        var stats = (Pf2eExtension)caster.SystemStats!;
        Assert.Equal(21, stats.SpellDc);          // 10 + INT 4 + level 5 + Trained 2
        Assert.Equal(11, stats.SpellAttackBonus);
    }

    [Fact]
    public async Task M50_ACasterWithAStoredDcButNoAttack_GetsTheAttackFromThatDc()
    {
        var caster = Caster(s => s.SpellDc = 22);

        await new Pf2eDeriveSpellcastingStep().ApplyAsync(
            new BootstrapContext { Character = caster, ActiveSystem = RulesetSystem.Pathfinder2e }, TestContext.Current.CancellationToken);

        Assert.Equal(22, ((Pf2eExtension)caster.SystemStats!).SpellDc);
        Assert.Equal(12, ((Pf2eExtension)caster.SystemStats!).SpellAttackBonus);
    }

    [Fact]
    public async Task M50_ASpellAttack_RollsTheSpellAttackNotTheWeaponOnes()
    {
        var caster = Caster(s => s.SpellAttackBonus = 10);
        var fake = Rolls(15, 3);

        var output = await new Pf2eRulesetResolver(fake).ResolveAsync(
            Context(caster, Dummy()), SpellAttack(), TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Equal(10, fake.RecordedRequests[0].Bonus);   // not STR 4 + level 5 + Trained 2
        Assert.Equal(0, fake.RecordedRequests[1].Bonus);    // no STR on a spell's damage
    }

    [Fact]
    public async Task M50_WithoutASpellAttack_TheDcMinusTenIsUsed_AndSaysSo()
    {
        var caster = Caster(s => s.SpellDc = 19);
        var fake = Rolls(15, 3);

        var output = await new Pf2eRulesetResolver(fake).ResolveAsync(
            Context(caster, Dummy()), SpellAttack(), TestContext.Current.CancellationToken);

        Assert.Equal(9, fake.RecordedRequests[0].Bonus);
        Assert.Contains("spell DC minus 10", output.Result.Narrative);
    }

    [Fact]
    public async Task M50_AnExplicitBonus_BeatsTheSheet()
    {
        var caster = Caster(s => s.SpellAttackBonus = 10);
        var fake = Rolls(15, 3);

        await new Pf2eRulesetResolver(fake).ResolveAsync(
            Context(caster, Dummy()), SpellAttack(("bonus", "3")), TestContext.Current.CancellationToken);

        Assert.Equal(3, fake.RecordedRequests[0].Bonus);
    }

    [Fact]
    public async Task M50_ARogueCastingAnAttackSpell_DoesNotSneakAttack()
    {
        var rogue = Caster(s => s.SpellAttackBonus = 6);
        rogue.ClassLevel = "Rogue 5";
        var fake = Rolls(15, 3);

        await new Pf2eRulesetResolver(fake).ResolveAsync(
            Context(rogue, Dummy()), SpellAttack(("sneakAttack", "true")), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(fake.RecordedRequests, r => r.Tag == "sneakAttack");
    }

    [Fact]
    public void M50_ALevelUp_MovesAStoredSpellDcAndAttackByTheFormulasChange()
    {
        var caster = Caster(s => { s.SpellDc = 20; s.SpellAttackBonus = 10; });
        var before = LevelUpDerivedStats.SnapshotPf2eSpellcasting(caster);
        var stats = (Pf2eExtension)caster.SystemStats!;
        stats.Level = 6;
        stats.IntelligenceMod = 5;

        var note = LevelUpDerivedStats.ApplyPf2eSpellcastingDelta(caster, before);

        Assert.Equal(22, stats.SpellDc);
        Assert.Equal(12, stats.SpellAttackBonus);
        Assert.NotNull(note);
    }

    [Fact]
    public void M50_ALevelUp_MovesAStatedDcByTheSameAmount_AndLeavesAStableOneAlone()
    {
        var stated = Caster(s => { s.SpellDc = 25; s.SpellAttackBonus = 15; });
        var statedBefore = LevelUpDerivedStats.SnapshotPf2eSpellcasting(stated);
        ((Pf2eExtension)stated.SystemStats!).Level = 6;
        LevelUpDerivedStats.ApplyPf2eSpellcastingDelta(stated, statedBefore);
        Assert.Equal(26, ((Pf2eExtension)stated.SystemStats!).SpellDc);   // one level of proficiency

        var same = Caster(s => s.SpellDc = 20);
        var sameBefore = LevelUpDerivedStats.SnapshotPf2eSpellcasting(same);
        Assert.Null(LevelUpDerivedStats.ApplyPf2eSpellcastingDelta(same, sameBefore));
        Assert.Equal(20, ((Pf2eExtension)same.SystemStats!).SpellDc);
    }

    // ---- M-51: multiple attack penalty ----

    private static RulesetAction Strike(string tags, params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "caster", TargetIds = ["dummy"], ActionType = RulesetActionType.Attack, ActionName = "Strike",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6", ["weaponTags"] = tags },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    private static async Task<int> AttackBonusOfStrikeNumber(int strikeNumber, RulesetAction strike)
    {
        var fake = Rolls(1, 1, 1, 1, 1, 1);
        var resolver = new Pf2eRulesetResolver(fake);
        var state = new CombatantState { ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };
        for (var i = 1; i < strikeNumber; i++)
        {
            Assert.True(resolver.TryConsumeActionSlot(state, Strike("melee"), out _));
        }

        Assert.True(resolver.TryConsumeActionSlot(state, strike, out _));
        await resolver.ResolveAsync(Context(Caster(), Dummy()), strike, TestContext.Current.CancellationToken);
        return fake.RecordedRequests[0].Bonus;
    }

    [Fact]
    public async Task M51_AgileWeapons_TakeMinusFourAndMinusEight_OthersMinusFiveAndMinusTen()
    {
        var first = await AttackBonusOfStrikeNumber(1, Strike("melee"));
        Assert.Equal(first - 5, await AttackBonusOfStrikeNumber(2, Strike("melee")));
        Assert.Equal(first - 10, await AttackBonusOfStrikeNumber(3, Strike("melee")));

        Assert.Equal(first - 4, await AttackBonusOfStrikeNumber(2, Strike("agile,finesse,melee")));
        Assert.Equal(first - 8, await AttackBonusOfStrikeNumber(3, Strike("agile,finesse,melee")));
    }

    [Fact]
    public async Task M51_AnExplicitMapPenalty_BeatsAgile()
    {
        var first = await AttackBonusOfStrikeNumber(1, Strike("melee"));

        Assert.Equal(first - 7, await AttackBonusOfStrikeNumber(2, Strike("agile,melee", ("mapPenalty", "7"))));
    }

    [Fact]
    public async Task M51_ASwordsItemTags_ReachTheActionAndMakeItAgile()
    {
        // The generated PF2e shortsword carries `agile` in its tags (RulesetData/pf2e/items/shortsword.yaml); this is how it reaches the resolver.
        var shortsword = new Item { Id = "items/shortsword", Name = "Shortsword", Tags = ["agile", "finesse", "martial", "melee", "versatile"] };
        var plain = Strike("melee");
        var agile = new RulesetAction
        {
            CharacterId = "caster", TargetIds = ["dummy"], ActionType = RulesetActionType.Attack, ActionName = "Strike",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d6" },
        };
        WeaponParameterResolver.ApplyWeaponItemProperties(agile, shortsword);

        var first = await AttackBonusOfStrikeNumber(1, plain);
        Assert.Equal(first - 4, await AttackBonusOfStrikeNumber(2, agile));   // STR 4 beats DEX 2 for both, so only the penalty differs
    }

    [Fact]
    public void M51_AnAttackSpell_CountsTowardsTheMultipleAttackPenalty_ASaveSpellDoesNot()
    {
        var resolver = new Pf2eRulesetResolver(Rolls());
        var state = new CombatantState { ActionBudget = new Dictionary<string, int> { ["actions"] = 3 } };

        var save = SpellAttack(("resolution", "save"), ("save", "Reflex"), ("dc", "18"));
        Assert.True(resolver.TryConsumeActionSlot(state, save, out _));
        Assert.False(save.Parameters.ContainsKey("priorStrikes") && save.Parameters["priorStrikes"] != "0");
        Assert.Equal(0, state.ActionBudget.GetValueOrDefault("strikesMade"));

        Assert.True(resolver.TryConsumeActionSlot(state, SpellAttack(), out _));
        Assert.Equal(1, state.ActionBudget["strikesMade"]);

        var strike = Strike("melee");
        Assert.True(resolver.TryConsumeActionSlot(state, strike, out _));
        Assert.Equal("1", strike.Parameters["priorStrikes"]);
    }

    [Fact]
    public async Task M51_ASaveSpellSentWithoutADc_IsASaveUsingTheSheetDc_NotAnAttack()
    {
        var caster = Caster(s => s.SpellDc = 18);
        var action = Fireball();
        action.Parameters.Remove("resolution");
        action.Parameters.Remove("dc");

        var output = await new Pf2eRulesetResolver(Rolls(10, 12)).ResolveAsync(
            Context(caster, Dummy()), action, TestContext.Current.CancellationToken);

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Contains("DC 18", output.Result.Narrative);
    }

    // ---- M-53: mitigation order ----

    private static RulesetAction Fireball(params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "caster", TargetIds = ["dummy"], ActionType = RulesetActionType.Spell, ActionName = "Fireball",
            DamageType = "Fire",
            Parameters = new Dictionary<string, string> { ["resolution"] = "save", ["save"] = "Reflex", ["dc"] = "18", ["damageDice"] = "6d6" },
        };
        foreach (var (k, v) in extra)
        {
            action.Parameters[k] = v;
        }

        return action;
    }

    private static async Task<(HpChange Hit, string Narrative)> SaveSpell(int saveRoll, int damage, int fireModifier)
    {
        var target = Dummy(tweak: s => s.DamageResistances = new() { ["Fire"] = fireModifier });
        var output = await new Pf2eRulesetResolver(Rolls(saveRoll, damage)).ResolveAsync(
            Context(Caster(), target), Fireball(), TestContext.Current.CancellationToken);
        Assert.True(output.Result.Success, output.Result.Narrative);
        return (output.Mutations.OfType<HpChange>().SingleOrDefault()!, output.Result.Narrative);
    }

    [Fact]
    public async Task M53_CriticalFailure_DoublesTheDamageBeforeAWeaknessAdds_AndIsFlaggedCritical()
    {
        var (hit, _) = await SaveSpell(saveRoll: 2, damage: 20, fireModifier: -5);   // 2 vs DC 18: critical failure

        Assert.Equal(-(20 * 2 + 5), hit.Delta);
        Assert.True(hit.Critical);
    }

    [Fact]
    public async Task M53_CriticalFailure_DoublesTheDamageBeforeAResistanceTakesOff()
    {
        var (hit, _) = await SaveSpell(saveRoll: 2, damage: 20, fireModifier: 5);

        Assert.Equal(-(20 * 2 - 5), hit.Delta);
    }

    [Fact]
    public async Task M53_Success_HalvesTheDamageThenTheResistanceTakesOff_AndIsNotCritical()
    {
        var (hit, _) = await SaveSpell(saveRoll: 18, damage: 20, fireModifier: 2);   // 18 vs DC 18: success

        Assert.Equal(-(20 / 2 - 2), hit.Delta);
        Assert.False(hit.Critical);
    }

    [Fact]
    public async Task M53_AStrikeCriticalHit_DoublesBeforeResistance()
    {
        var target = Dummy(tweak: s => s.DamageResistances = new() { ["Physical"] = 5 });
        var strike = Strike("melee", ("bonus", "0"));
        strike.DamageType = "Physical";

        var output = await new Pf2eRulesetResolver(Rolls(25, 6)).ResolveAsync(
            Context(Caster(), target), strike, TestContext.Current.CancellationToken);

        var hit = output.Mutations.OfType<HpChange>().Single();
        Assert.Equal(-(6 * 2 - 5), hit.Delta);   // (not 6 - 5, doubled)
        Assert.True(hit.Critical);
    }

    // ---- M-52: dying, wounded, doomed ----

    private static Character Pc(int hp = 20, int max = 20, Action<Pf2eExtension>? tweak = null)
    {
        var stats = new Pf2eExtension { Level = 5 };
        tweak?.Invoke(stats);
        return new Character { Id = "chars/pip", Name = "Pip", MaxHp = max, CurrentHp = hp, IsPc = true, SystemStats = stats, CurrentLocationId = "locations/gorse" };
    }

    private static ChangeContext Ctx(Character c, List<string>? summary = null) =>
        ChangeContextTestHelper.Create(
            characters: new() { [c.Id] = c }, summary: summary ?? [],
            dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance));

    private static Task<ChangeHandlerResult> Hit(Character c, int delta, bool critical = false, List<string>? summary = null) =>
        new HpChangeHandler(Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = c.Id, Delta = delta, Critical = critical }, Ctx(c, summary), TestContext.Current.CancellationToken);

    private static Task<ChangeHandlerResult> Recovery(Character c, int die, List<string>? summary = null) =>
        new DeathSaveChangeHandler(Substitute.For<IRollService>())
            .ApplyAsync(new DeathSaveChange { CharacterId = c.Id, Roll = die }, Ctx(c, summary), TestContext.Current.CancellationToken);

    private static int Counter(Character c, string name) => Pf2eDying.Get(c, name);

    [Fact]
    public async Task M52_ReachingZero_GivesDyingOne_AndKeepsThe5eTallyOff()
    {
        var pc = Pc(hp: 5);

        await Hit(pc, -5);

        Assert.Equal(1, Counter(pc, Pf2eDying.Dying));
        Assert.Null(pc.DeathSaves);
        Assert.True(HitPointState.IsDying(pc));
        Assert.True(ActionBlock.IsBlocked(pc, out _));
    }

    [Fact]
    public async Task M52_ACriticalHit_GivesDyingTwo()
    {
        var pc = Pc(hp: 5);

        await Hit(pc, -9, critical: true);

        Assert.Equal(2, Counter(pc, Pf2eDying.Dying));
    }

    [Fact]
    public async Task M52_Wounded_AddsToTheDyingValueWhenKnockedOut()
    {
        var pc = Pc(hp: 5);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 2);

        await Hit(pc, -5);

        Assert.Equal(3, Counter(pc, Pf2eDying.Dying));   // 1 + wounded 2
        Assert.Equal(2, Counter(pc, Pf2eDying.Wounded));
    }

    [Fact]
    public async Task M52_DamageWhileDying_RaisesDyingByOne_TwoOnACritical_WithoutAddingWoundedAgain()
    {
        var pc = Pc(hp: 5);
        await Hit(pc, -5);

        await Hit(pc, -3);
        Assert.Equal(2, Counter(pc, Pf2eDying.Dying));

        await Hit(pc, -3);
        Assert.Equal(3, Counter(pc, Pf2eDying.Dying));

        await Hit(pc, -3, critical: true);
        Assert.True(pc.IsDead);   // 2 + a critical hit's 2 is dying 4
        Assert.Equal(0, Counter(pc, Pf2eDying.Wounded));
    }

    [Fact]
    public async Task M52_DyingFour_IsDeath()
    {
        var pc = Pc(hp: 5);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 3);

        await Hit(pc, -5);   // 1 + wounded 3 = 4

        Assert.True(pc.IsDead);
    }

    [Fact]
    public async Task M52_Doomed_LowersTheDyingValueThatKills()
    {
        var pc = Pc(hp: 5);
        Pf2eDying.Set(pc, Pf2eDying.Doomed, 1);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 2);

        await Hit(pc, -5);   // dying 3 kills a doomed 1 character

        Assert.True(pc.IsDead);
    }

    [Fact]
    public async Task M52_HugeDamage_IsStillOnlyDyingOne_NoInstantDeathRuleIsGuessed()
    {
        var pc = Pc(hp: 5, max: 20);

        await Hit(pc, -25);

        Assert.False(pc.IsDead);
        Assert.Equal(1, Counter(pc, Pf2eDying.Dying));
    }

    [Fact]
    public async Task M52_TemporaryHitPoints_ThatTakeTheWholeHit_StartNothing()
    {
        var pc = Pc(hp: 5);
        pc.TempHp = 10;

        await Hit(pc, -8);

        Assert.Equal(5, pc.CurrentHp);
        Assert.Equal(0, Counter(pc, Pf2eDying.Dying));
    }

    [Fact]
    public async Task M52_AnNpcWhoDropsToZero_GetsNoDyingCondition()
    {
        var npc = Pc(hp: 5);
        npc.IsPc = false;

        await Hit(npc, -5);

        Assert.Equal(0, Counter(npc, Pf2eDying.Dying));
    }

    [Theory]
    [InlineData(2, 12, 1)]    // DC 12, exactly: success, dying 2 -> 1
    [InlineData(2, 11, 3)]    // 11 vs 12: failure, +1
    [InlineData(2, 2, -1)]    // 2 vs 12: critical failure, +2 -> dying 4: dead
    [InlineData(1, 11, 0)]    // DC 11, exactly: success, dying 1 -> 0: dying ends
    public async Task M52_RecoveryCheck_IsAFlatCheckAgainstTenPlusDying(int dying, int die, int expected)
    {
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, dying);

        var result = await Recovery(pc, die);

        Assert.True(result.Success);
        if (expected < 0)
        {
            Assert.True(pc.IsDead);
        }
        else
        {
            Assert.Equal(expected, Counter(pc, Pf2eDying.Dying));
        }
    }

    [Fact]
    public async Task M52_ANaturalTwenty_ImprovesTheResultOneStep_SoACriticalSuccessIsReachable()
    {
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, 3);   // DC 13: a 20 is a success by 7, which the natural 20 lifts to a critical success (-2)

        await Recovery(pc, 20);

        Assert.Equal(1, Counter(pc, Pf2eDying.Dying));
    }

    [Fact]
    public async Task M52_ANaturalOne_IsACriticalFailure()
    {
        // At DC 11 and up a 1 is already a critical failure, so this does not exercise the one-step shift; Degree() is pinned below.
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, 1);

        await Recovery(pc, 1);

        Assert.Equal(3, Counter(pc, Pf2eDying.Dying));
    }

    [Fact]
    public async Task M52_DyingFallingToZero_EndsItAndAddsWounded_ButLeavesThemUnconsciousAtZero()
    {
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, 1);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 1);

        await Recovery(pc, 15);   // DC 11: success

        Assert.Equal(0, Counter(pc, Pf2eDying.Dying));
        Assert.Equal(2, Counter(pc, Pf2eDying.Wounded));
        Assert.Equal(0, pc.CurrentHp);
        Assert.False(HitPointState.IsDying(pc));   // no more turns, no more checks
    }

    [Fact]
    public async Task M52_ARecoveryCheck_IsRefusedForSomeoneWhoIsNotDying()
    {
        var pc = Pc(hp: 0);

        var result = await Recovery(pc, 12);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task M52_Healing_EndsDying_AndAddsWounded()
    {
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, 2);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 1);
        var messages = new List<string>();

        await Hit(pc, 5, summary: messages);

        Assert.Equal(5, pc.CurrentHp);
        Assert.Equal(0, Counter(pc, Pf2eDying.Dying));
        Assert.Equal(2, Counter(pc, Pf2eDying.Wounded));
        Assert.Contains(messages, m => m.Contains("wounded 2"));
    }

    [Fact]
    public void M52_StaleDying_IsClearedWhenHitPointsAreRaisedDirectly_SoItCannotLockAnActivePcOut()
    {
        var pc = Pc(hp: 0);
        Pf2eDying.Set(pc, Pf2eDying.Dying, 2);
        pc.CurrentHp = 7;   // a revival or an upsert, not a heal through HitPointState.Set

        HitPointState.SyncDowned(pc);

        Assert.Equal(0, Counter(pc, Pf2eDying.Dying));
        Assert.False(ActionBlock.IsBlocked(pc, out _));
    }

    [Fact]
    public void M52_Degree_ANaturalTwentyMovesOneStepUp_ANaturalOneOneStepDown()
    {
        Assert.Equal(RecoveryDegree.Failure, Pf2eDying.Degree(13, 4));          // 13 vs DC 14
        Assert.Equal(RecoveryDegree.CriticalSuccess, Pf2eDying.Degree(20, 4));   // DC 14 met by 6 is a success, lifted
        Assert.Equal(RecoveryDegree.Success, Pf2eDying.Degree(20, 12));          // 20 vs DC 22 is a failure, lifted
        Assert.Equal(RecoveryDegree.CriticalFailure, Pf2eDying.Degree(1, 1));    // already a critical failure, stays one
    }

    [Fact]
    public async Task M52_HealingSomeoneWhoWasNeverDying_AddsNoWounded()
    {
        var pc = Pc(hp: 10);

        await Hit(pc, 5);

        Assert.Equal(0, Counter(pc, Pf2eDying.Wounded));
    }

    [Fact]
    public async Task M52_AFreshHitOnAnUnconsciousButNotDyingCharacter_KnocksThemOutAgain()
    {
        var pc = Pc(hp: 0);
        HitPointState.SyncDowned(pc);
        Pf2eDying.Set(pc, Pf2eDying.Wounded, 1);

        await Hit(pc, -3);

        Assert.Equal(2, Counter(pc, Pf2eDying.Dying));   // 1 + wounded 1
    }

    [Fact]
    public async Task M52_Pf2eDamage_NeverCallsForAConcentrationSave()
    {
        var pc = Pc(hp: 30);
        pc.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Concentration: Fear", Category = "Buff" });
        var rolls = Substitute.For<IRollService>();

        await new HpChangeHandler(rolls).ApplyAsync(
            new HpChange { CharacterId = pc.Id, Delta = -4 }, Ctx(pc), TestContext.Current.CancellationToken);

        await rolls.DidNotReceive().RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>());
        Assert.Contains(pc.SystemStats.StatusEffects, e => e.Name.StartsWith("Concentration"));
    }

    [Fact]
    public async Task M52_ALongRest_EndsWoundedForSomeoneAtFullHitPoints_ButNotForAHurtOne()
    {
        foreach (var (hp, expectedWounded) in new[] { (20, 0), (12, 2) })
        {
            var pc = Pc(hp: hp, max: 20);
            Pf2eDying.Set(pc, Pf2eDying.Wounded, 2);
            var handler = new RestChangeHandler(new EncounterResolver(() => 1.0), RulesetDataTestHelper.CreateConditionProvider());
            var context = ChangeContextTestHelper.Create(
                characters: new() { [pc.Id] = pc },
                locations: new() { ["locations/gorse"] = new Location { Id = "locations/gorse", Type = LocationType.Settlement } },
                summary: []);

            var result = await handler.ApplyAsync(
                new RestChange { CharacterId = pc.Id, LocationId = "locations/gorse", IntendedHours = 8, RestType = RestType.LongRest },
                context, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(expectedWounded, Counter(pc, Pf2eDying.Wounded));
        }
    }
}
