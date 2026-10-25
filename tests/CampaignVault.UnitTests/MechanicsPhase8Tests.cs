using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Phase 8 of the mechanics remediation: the long rest heals and returns hit dice (M-70), and exhaustion needs food and drink (M-71).</summary>
public class MechanicsPhase8Tests
{
    private static async Task<(Character Character, List<string> Summary, string? Result)> LongRestAsync(Character character, RestType type = RestType.LongRest)
    {
        var summary = new List<string>();
        var dispatcher = new WorldChangeDispatcher(
            [RulesetDataTestHelper.CreateStatusChangeHandler(), new ResourceChangeHandler()],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var handler = new RestChangeHandler(new EncounterResolver(() => 1.0), RulesetDataTestHelper.CreateConditionProvider());
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character },
            locations: new Dictionary<string, Location> { ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Settlement } },
            logger: NullLogger.Instance,
            summary: summary,
            dispatcher: dispatcher);

        var result = await handler.ApplyAsync(new RestChange
        {
            CharacterId = character.Id,
            LocationId = "loc/1",
            IntendedHours = type == RestType.LongRest ? 8 : 1,
            RestType = type,
        }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        return (character, summary, result.Message);
    }

    private static Character Fighter(int hp, int level = 4, int dice = 4, int diceMax = 4) => new()
    {
        Id = "chars/f", Name = "Brea", IsPc = true, CurrentLocationId = "loc/1", MaxHp = 40, CurrentHp = hp,
        SystemStats = new Dnd5eExtension
        {
            Level = level,
            ResourcePools = new Dictionary<string, ResourcePool> { ["hit_dice"] = new() { Current = dice, Max = diceMax, Recovery = RecoveryType.Never } },
        },
    };

    // M-70

    [Fact]
    public async Task LongRest_5e_RestoresHitPointsToMaximum()
    {
        var (c, summary, _) = await LongRestAsync(Fighter(12));

        Assert.Equal(40, c.CurrentHp);
        Assert.Contains(summary, m => m.Contains("regains 28 hit points", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRest_5e_RespectsLoweredMaximumFromExhaustionFour()
    {
        var c = Fighter(10);
        c.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Exhaustion 5", ConditionName = "exhaustion", Category = "Condition" });

        await LongRestAsync(c);

        // Exhaustion 5 drops to 4 on the rest, and level 4 halves the maximum: 20.
        Assert.Equal(20, c.CurrentHp);
    }

    [Fact]
    public async Task LongRest_RegainsHalfTheHitDice()
    {
        var (c, _, _) = await LongRestAsync(Fighter(40, level: 6, dice: 0, diceMax: 6));

        Assert.Equal(3, c.SystemStats!.ResourcePools!["hit_dice"].Current);
    }

    [Fact]
    public async Task LongRest_RegainsAtLeastOneHitDie()
    {
        var (c, _, _) = await LongRestAsync(Fighter(40, level: 1, dice: 0, diceMax: 1));

        Assert.Equal(1, c.SystemStats!.ResourcePools!["hit_dice"].Current);
    }

    [Fact]
    public async Task LongRest_HitDice_NeverGoPastTheMaximum()
    {
        var (c, _, _) = await LongRestAsync(Fighter(40, level: 6, dice: 5, diceMax: 6));

        Assert.Equal(6, c.SystemStats!.ResourcePools!["hit_dice"].Current);
    }

    [Fact]
    public async Task ShortRest_HealsNothingAndSaysHowToSpendHitDice()
    {
        var (c, _, message) = await LongRestAsync(Fighter(12), RestType.ShortRest);

        Assert.Equal(12, c.CurrentHp);
        Assert.Equal(4, c.SystemStats!.ResourcePools!["hit_dice"].Current);
        Assert.Contains("hit_dice", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongRest_CharacterWithoutHitDicePool_StillHeals()
    {
        var c = Fighter(12);
        c.SystemStats!.ResourcePools!.Clear();

        await LongRestAsync(c);

        Assert.Equal(40, c.CurrentHp);
    }

    [Fact]
    public async Task LongRest_DyingCharacter_IsNotHealed()
    {
        var c = Fighter(0);
        c.DeathSaves = new DeathSaveTally();

        var (_, summary, _) = await LongRestAsync(c);

        Assert.Equal(0, c.CurrentHp);
        Assert.Contains(summary, m => m.Contains("is dying", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRest_StableCharacterAtZero_ComesRoundAtOneHpWithoutTheRestsBenefit()
    {
        // SRD: a long rest needs at least 1 HP at its start; a stable creature regains 1 HP after 1d4 hours on its own.
        var c = Fighter(0);
        c.DeathSaves = new DeathSaveTally { Stable = true };

        var (_, summary, _) = await LongRestAsync(c);

        Assert.Equal(1, c.CurrentHp);
        Assert.Null(c.DeathSaves);
        Assert.Contains(summary, m => m.Contains("gives no other benefit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRest_Pf2e_HealsConstitutionTimesLevelNotEverything()
    {
        var c = new Character
        {
            Id = "chars/p", Name = "Ysa", IsPc = true, CurrentLocationId = "loc/1", MaxHp = 60, CurrentHp = 10,
            SystemStats = new Pf2eExtension { Level = 4, ConstitutionMod = 3 },
        };

        await LongRestAsync(c);

        Assert.Equal(22, c.CurrentHp); // 10 + 3 x 4
    }

    // M-71

    private static Character Exhausted(float hunger, float thirst)
    {
        var c = Fighter(40);
        c.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Exhaustion 2", ConditionName = "exhaustion", Category = "Condition" });
        c.Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = hunger, ["thirst"] = thirst } };
        return c;
    }

    [Fact]
    public async Task LongRest_Hungry_KeepsExhaustionAndSaysWhy()
    {
        var (c, summary, _) = await LongRestAsync(Exhausted(hunger: 95, thirst: 5));

        Assert.Contains(c.SystemStats!.StatusEffects, e => e.Name == "Exhaustion 2");
        Assert.Contains(summary, m => m.Contains("food", StringComparison.Ordinal) && m.Contains("stays", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRest_Thirsty_KeepsExhaustion()
    {
        var (c, summary, _) = await LongRestAsync(Exhausted(hunger: 5, thirst: 95));

        Assert.Contains(c.SystemStats!.StatusEffects, e => e.Name == "Exhaustion 2");
        Assert.Contains(summary, m => m.Contains("drink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRest_FedAndWatered_LowersExhaustion()
    {
        var (c, _, _) = await LongRestAsync(Exhausted(hunger: 40, thirst: 40));

        Assert.Contains(c.SystemStats!.StatusEffects, e => e.Name == "Exhaustion 1");
    }

    [Fact]
    public async Task LongRest_Hungry_StillHealsAndClearsOtherConditions()
    {
        var c = Exhausted(hunger: 95, thirst: 95);
        c.CurrentHp = 5;

        await LongRestAsync(c);

        Assert.Equal(40, c.CurrentHp);
    }

    [Fact]
    public async Task LongRest_Pf2eDoomed_DoesNotNeedFood()
    {
        var c = new Character
        {
            Id = "chars/p", Name = "Ysa", IsPc = true, CurrentLocationId = "loc/1", MaxHp = 60, CurrentHp = 60,
            SystemStats = new Pf2eExtension { Level = 4, ConstitutionMod = 3 },
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 95, ["thirst"] = 95 } },
        };
        c.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Doomed 2", ConditionName = "doomed", Category = "Condition" });

        await LongRestAsync(c);

        Assert.Contains(c.SystemStats.StatusEffects, e => e.Name == "Doomed 1");
    }

    [Fact]
    public async Task LongRest_Decrement_KeepsTurnStartExpiryAndPendingDamage()
    {
        var c = Fighter(40);
        c.SystemStats!.StatusEffects.Add(new StatusEffect
        {
            Name = "Exhaustion 2", ConditionName = "exhaustion", Category = "Condition", ExpiresAtOwnTurnStart = true,
            PendingDamage = new PendingEffectDamage { DiceExpression = "1d4", DamageType = "fire" },
        });

        await LongRestAsync(c);

        var kept = Assert.Single(c.SystemStats.StatusEffects, e => e.Name == "Exhaustion 1");
        Assert.True(kept.ExpiresAtOwnTurnStart);
        Assert.Equal("1d4", kept.PendingDamage?.DiceExpression);
    }
}
