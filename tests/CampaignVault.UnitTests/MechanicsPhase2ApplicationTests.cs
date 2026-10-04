using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Remediation plan phase 2 (M-11, M-13, M-15): damage lands in one place, with temp HP and hit instances.</summary>
public class MechanicsPhase2ApplicationTests
{
    private static Character Pc(int hp = 20, int max = 20) => new()
    {
        Id = "chars/maeve", Name = "Maeve", MaxHp = max, CurrentHp = hp, IsPc = true,
        SystemStats = new Dnd5eExtension(), CurrentLocationId = "locations/gorse",
    };

    private static ChangeContext Ctx(Character c) =>
        ChangeContextTestHelper.Create(characters: new() { [c.Id] = c }, summary: []);

    private static Task<ChangeHandlerResult> Hit(Character c, int delta, bool critical = false, IRollService? rolls = null) =>
        new HpChangeHandler(rolls ?? Substitute.For<IRollService>())
            .ApplyAsync(new HpChange { CharacterId = c.Id, Delta = delta, Critical = critical }, Ctx(c), TestContext.Current.CancellationToken);

    private static Task<ChangeHandlerResult> Grant(Character c, int amount, string? source = null) =>
        new TempHpGrantHandler()
            .ApplyAsync(new TempHpGrant { CharacterId = c.Id, Amount = amount, Source = source }, Ctx(c), TestContext.Current.CancellationToken);

    // ---- M-13: hit instances ----

    [Fact]
    public async Task CriticalHit_AtZeroHp_CountsTwoFailures()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();

        await Hit(pc, -4, critical: true);

        Assert.Equal(2, pc.DeathSaves!.Failures);
        Assert.False(pc.IsDead);
    }

    [Fact]
    public async Task CriticalHit_AtZeroHp_WithOneFailure_Kills()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally { Failures = 1 };

        await Hit(pc, -4, critical: true);

        Assert.True(pc.IsDead);
        Assert.Equal("failed death saves", pc.Death!.Cause);
    }

    [Fact]
    public async Task OrdinaryHits_AtZeroHp_AreOneFailureEach()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally();

        await Hit(pc, -4);
        await Hit(pc, -4);

        Assert.Equal(2, pc.DeathSaves!.Failures);
    }

    [Fact]
    public void HpChange_Critical_IsOptionalInJson_AndOmittedWhenFalse()
    {
        var plain = System.Text.Json.JsonSerializer.Deserialize<WorldChange>("""{"$type":"hp","characterId":"chars/a","delta":-3}""");
        Assert.False(Assert.IsType<HpChange>(plain).Critical);

        var json = System.Text.Json.JsonSerializer.Serialize<WorldChange>(new HpChange { CharacterId = "chars/a", Delta = -3 });
        Assert.DoesNotContain("critical", json);
    }

    // ---- M-15: temporary hit points ----

    [Fact]
    public async Task TempHp_AbsorbDamageFirst()
    {
        var pc = Pc(hp: 10);
        await Grant(pc, 8, "False Life");

        await Hit(pc, -5);
        Assert.Equal((10, 3), (pc.CurrentHp, pc.TempHp));

        await Hit(pc, -6);
        Assert.Equal((7, null), (pc.CurrentHp, pc.TempHp));
        Assert.Null(pc.TempHpSource);
    }

    [Fact]
    public async Task TempHp_DoNotStack_LargerWins()
    {
        var pc = Pc();
        await Grant(pc, 5, "False Life");
        await Grant(pc, 3, "Heroism");
        Assert.Equal((5, "False Life"), (pc.TempHp, pc.TempHpSource));

        await Grant(pc, 9, "Aid");
        Assert.Equal((9, "Aid"), (pc.TempHp, pc.TempHpSource));
    }

    [Fact]
    public async Task TempHp_AreNotRestoredByHealing()
    {
        var pc = Pc(hp: 10);
        await Grant(pc, 4);

        await Hit(pc, 6);

        Assert.Equal((16, 4), (pc.CurrentHp, pc.TempHp));
    }

    [Fact]
    public async Task TempHp_TakingTheWholeHit_StartsNoDeathSaves_AndMassiveDamageCountsOnlyWhatReachedHp()
    {
        var pc = Pc(hp: 3, max: 20);
        await Grant(pc, 30);

        await Hit(pc, -25); // all of it absorbed: nothing reaches hit points
        Assert.Equal((3, 5), (pc.CurrentHp, pc.TempHp));
        Assert.Null(pc.DeathSaves);

        await Hit(pc, -40); // 5 absorbed, 35 reaches HP: 3 to reach 0, 32 over max HP 20
        Assert.True(pc.IsDead);
    }

    [Fact]
    public async Task TempHp_Grant_RejectsNonPositive_AndTheDead()
    {
        var pc = Pc();
        Assert.False((await Grant(pc, 0)).Success);
        Assert.False((await Grant(pc, -3)).Success);

        pc.Death = new DeathRecord { Day = 1 };
        Assert.False((await Grant(pc, 5)).Success);
    }

    [Fact]
    public void TempHpGrant_RoundTripsThroughPolymorphicJson()
    {
        var change = System.Text.Json.JsonSerializer.Deserialize<WorldChange>(
            """{"$type":"grant_temp_hp","characterId":"chars/maeve","amount":9,"source":"False Life"}""");
        var grant = Assert.IsType<TempHpGrant>(change);
        Assert.Equal((9, "False Life"), (grant.Amount, grant.Source));
    }

    // ---- M-11: the delayed tick uses the same path ----

    private static StatusEffect Residue(string dice = "2d4") => new()
    {
        Name = "AcidArrowResidue",
        Category = "Condition",
        PendingDamage = new PendingEffectDamage { DiceExpression = dice, DamageType = "acid" },
        ExpiresAtOwnTurnStart = true,
        AppliedBy = "system/combat-resolver",
    };

    private static IRollService Rolls(params int[] results)
    {
        var rolls = new FakeRollService();
        foreach (var r in results)
            rolls.NextRolls.Enqueue(new RollOutcome { Result = r, Summary = $"Rolled {r}" });
        return rolls;
    }

    [Fact]
    public async Task Tick_DroppingAPcToZero_StartsDeathSaves()
    {
        var pc = Pc(hp: 4);
        pc.SystemStats!.StatusEffects.Add(Residue());
        var messages = new List<string>();

        await DelayedTickProcessor.ProcessAsync(pc, Rolls(7), messages, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, pc.CurrentHp);
        Assert.NotNull(pc.DeathSaves);
        Assert.Contains(messages, m => m.Contains("dying"));
    }

    [Fact]
    public async Task Tick_MassiveDamage_KillsAPc_AndPublishesTheDeath()
    {
        var pc = Pc(hp: 4, max: 10);
        pc.SystemStats!.StatusEffects.Add(Residue());

        var events = await DelayedTickProcessor.ProcessAsync(
            pc, Rolls(20), [], currentDay: () => Task.FromResult(3), ct: TestContext.Current.CancellationToken);

        Assert.True(pc.IsDead);
        Assert.Equal(3, pc.Death!.Day);
        Assert.Contains(events, e => e.Topic == CampaignVault.Events.CoreEvents.CharacterDied);
    }

    [Fact]
    public async Task Tick_AppliesStatBlockResistance_AndTempHp()
    {
        var pc = Pc(hp: 10);
        ((Dnd5eExtension)pc.SystemStats!).DamageModifiers["acid"] = 0.5f;
        pc.SystemStats.StatusEffects.Add(Residue());
        pc.TempHp = 2;

        await DelayedTickProcessor.ProcessAsync(pc, Rolls(10), [], ct: TestContext.Current.CancellationToken);

        Assert.Equal(7, pc.CurrentHp); // 10 acid, halved to 5, two absorbed
        Assert.Null(pc.TempHp);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(1, false)]
    public async Task TempHp_FadeOnALongRest_NotAShortOne(int hours, bool fades)
    {
        var sleeper = new Character
        {
            Id = "chars/aang", Name = "Aang", CurrentLocationId = "locations/pond", MaxHp = 10, CurrentHp = 10,
            TempHp = 6, TempHpSource = "False Life",
        };
        var context = ChangeContextTestHelper.Create(campaignName: "test",
            characters: new Dictionary<string, Character> { [sleeper.Id] = sleeper },
            locations: new Dictionary<string, Location> { ["locations/pond"] = new() { Id = "locations/pond", Name = "Pond" } });
        var rest = new RestChange
        {
            CharacterId = sleeper.Id, LocationId = "locations/pond", IntendedHours = hours,
            RestType = hours >= 8 ? RestType.LongRest : RestType.ShortRest,
        };

        await RulesetDataTestHelper.CreateRestChangeHandler(new EncounterResolver(() => 0.99))
            .ApplyAsync(rest, context, TestContext.Current.CancellationToken);

        Assert.Equal(fades ? null : 6, sleeper.TempHp);
    }

    // ---- HitPointState ----

    [Fact]
    public void HitPointState_Set_ClearsTally_WhenRisingAboveZero()
    {
        var pc = Pc(hp: 0);
        pc.DeathSaves = new DeathSaveTally { Failures = 2 };

        var transition = HitPointState.Set(pc, 6);

        Assert.True(transition.Recovered);
        Assert.Null(pc.DeathSaves);
    }

    [Fact]
    public void HitPointState_Set_StartsTally_WhenDroppingToZero_ForAPc_NotAnNpc()
    {
        var pc = Pc(hp: 5);
        Assert.True(HitPointState.Set(pc, 0).WentDown);
        Assert.NotNull(pc.DeathSaves);

        var npc = Pc(hp: 5);
        npc.IsPc = false;
        HitPointState.Set(npc, 0);
        Assert.Null(npc.DeathSaves);
    }

    [Fact]
    public void HitPointState_Set_ClampsToMaxHp()
    {
        var pc = Pc(hp: 5, max: 12);
        HitPointState.Set(pc, 99);
        Assert.Equal(12, pc.CurrentHp);
    }
}
