using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class SurvivalDeprivationRuleTests
{
    private static readonly ConditionDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_survivaldeprivation_test_" + Guid.NewGuid()),
        typeof(ConditionDefinitionProvider).Assembly);

    private static Character MakeCharacter(SystemExtension stats, float hunger = 0f, float thirst = 0f)
    {
        var character = new Character
        {
            Id = "chars/test_" + Guid.NewGuid().ToString("N"),
            Name = "Test Character",
            MaxHp = 10,
            CurrentHp = 10,
            SystemStats = stats,
        };
        character.Needs.ActiveNeeds["hunger"] = hunger;
        character.Needs.ActiveNeeds["thirst"] = thirst;
        return character;
    }

    private static Task<RuleResult> RunRule(Character character, double daysPassed, CampaignConfig? config = null)
    {
        var rule = new SurvivalDeprivationRule(Provider);
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 10 },
            [],
            [character],
            null!,
            daysPassed,
            "test_campaign",
            Config: config);

        return rule.ApplyAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ApplyAsync_SevereHunger_AccruesStreakButDoesNotEscalateBeforeTolerance()
    {
        var character = MakeCharacter(new Dnd5eExtension(), hunger: 95f);

        var result = await RunRule(character, daysPassed: 1);

        var streak = Assert.Single(result.Deltas.OfType<AttributeChange>(), d => d.Attribute == "deprivation_streak_hunger");
        Assert.True(streak.IsDelta);
        Assert.Equal(1f, streak.Value);
        Assert.Empty(result.Deltas.OfType<StatusChange>());
    }

    [Fact]
    public async Task ApplyAsync_HungerStreakCrossesTolerance_EscalatesDnd5eExhaustion()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 2.5f } };
        var character = MakeCharacter(stats, hunger: 95f);

        // Default food tolerance is 3 days; 2.5 -> 3.5 crosses the "3 whole days past tolerance" boundary.
        var result = await RunRule(character, daysPassed: 1);

        var statusChange = Assert.Single(result.Deltas.OfType<StatusChange>());
        Assert.Equal("exhaustion", statusChange.Effect!.ConditionName);
        Assert.Equal("Exhaustion 1", statusChange.Effect!.Name);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("prolonged hunger"));
    }

    [Fact]
    public async Task ApplyAsync_ExistingStackingCondition_IncrementsLevel()
    {
        var stats = new Dnd5eExtension
        {
            Attributes = { ["deprivation_streak_thirst"] = 0.5f },
            StatusEffects = [new StatusEffect { Name = "Exhaustion 2", ConditionName = "exhaustion", Category = "Condition" }],
        };
        var character = MakeCharacter(stats, thirst: 95f);

        // Default water tolerance is 1 day; 0.5 -> 1.5 crosses the boundary once.
        var result = await RunRule(character, daysPassed: 1);

        var removed = Assert.Single(result.Deltas.OfType<StatusRemove>());
        Assert.Equal("Exhaustion 2", removed.Status);

        var added = Assert.Single(result.Deltas.OfType<StatusChange>());
        Assert.Equal("Exhaustion 3", added.Effect!.Name);
    }

    [Fact]
    public async Task ApplyAsync_Pf2eFatigued_IsAppliedOnceAndNotReapplied()
    {
        var stats = new Pf2eExtension
        {
            Attributes = { ["deprivation_streak_hunger"] = 2.5f },
            StatusEffects = [new StatusEffect { Name = "Fatigued", ConditionName = "fatigued", Category = "Condition" }],
        };
        var character = MakeCharacter(stats, hunger: 95f);

        var result = await RunRule(character, daysPassed: 1);

        Assert.Empty(result.Deltas.OfType<StatusChange>());
        Assert.Empty(result.Deltas.OfType<StatusRemove>());
    }

    [Fact]
    public async Task ApplyAsync_RecoveredHunger_ResetsStreakToZero()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 4f } };
        var character = MakeCharacter(stats, hunger: 5f); // well below RecoveryHunger

        var result = await RunRule(character, daysPassed: 1);

        var reset = Assert.Single(result.Deltas.OfType<AttributeChange>(), d => d.Attribute == "deprivation_streak_hunger");
        Assert.False(reset.IsDelta);
        Assert.Equal(0f, reset.Value);
    }

    [Fact]
    public async Task ApplyAsync_MidBandHunger_HoldsStreakSteady()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 2f } };
        var character = MakeCharacter(stats, hunger: 50f); // between recovery (20) and severe (90)

        var result = await RunRule(character, daysPassed: 1);

        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task ApplyAsync_ExtremeTemperature_EscalatesLikeHungerOrThirst()
    {
        var stats = new Dnd5eExtension
        {
            Temperature = -25f, // below SevereCold (-20)
            Attributes = { ["deprivation_streak_temperature"] = 0.5f },
        };
        var character = MakeCharacter(stats);

        // Default temperature tolerance is 1 day; 0.5 -> 1.5 crosses the boundary.
        var result = await RunRule(character, daysPassed: 1);

        var statusChange = Assert.Single(result.Deltas.OfType<StatusChange>());
        Assert.Equal("exhaustion", statusChange.Effect!.ConditionName);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("extreme temperature exposure"));
    }

    [Fact]
    public async Task ApplyAsync_SurvivalConsequencesDisabled_NoOp()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 10f } };
        var character = MakeCharacter(stats, hunger: 95f);

        var result = await RunRule(character, daysPassed: 1, config: new CampaignConfig { SurvivalConsequencesEnabled = false });

        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task ApplyAsync_MultiDayJump_EscalatesMultipleLevelsAtOnce()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 0f } };
        var character = MakeCharacter(stats, hunger: 95f);

        // Default food tolerance is 3 days; a 5-day jump lands the streak at 5, i.e. 2 days past tolerance.
        var result = await RunRule(character, daysPassed: 5);

        var statusChange = Assert.Single(result.Deltas.OfType<StatusChange>());
        Assert.Equal("Exhaustion 2", statusChange.Effect!.Name);
    }

    [Fact]
    public async Task ApplyAsync_DeadCharacter_Skipped()
    {
        var stats = new Dnd5eExtension { Attributes = { ["deprivation_streak_hunger"] = 10f } };
        var character = MakeCharacter(stats, hunger: 95f);
        character.CurrentHp = 0;

        var result = await RunRule(character, daysPassed: 1);

        Assert.Empty(result.Deltas);
    }
}
