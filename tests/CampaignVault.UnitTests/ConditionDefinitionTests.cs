using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class ConditionDefinitionTests
{
    private static readonly ConditionDefinitionProvider Provider = new(
        Path.Combine(Path.GetTempPath(), "cv_conditiondef_test_" + Guid.NewGuid()),
        typeof(ConditionDefinitionProvider).Assembly);

    // ── ConditionDefinition.Merge ─────────────────────────────────────────────

    [Fact]
    public void Merge_ChildDurationTypeWins_OverParent()
    {
        var parent = new ConditionDefinition { Name = "base_cond", DurationType = ConditionDurationType.Manual };
        var child = new ConditionDefinition { Name = "sub_cond", Inherits = ["base_cond"], DurationType = ConditionDurationType.Timed };

        var merged = ConditionDefinition.Merge(child, parent);

        Assert.Equal(ConditionDurationType.Timed, merged.DurationType);
    }

    [Fact]
    public void Merge_NullDurationType_InheritsFromParent()
    {
        var parent = new ConditionDefinition { Name = "base_cond", DurationType = ConditionDurationType.Manual };
        var child = new ConditionDefinition { Name = "sub_cond", Inherits = ["base_cond"], DurationType = null };

        var merged = ConditionDefinition.Merge(child, parent);

        Assert.Equal(ConditionDurationType.Manual, merged.DurationType);
    }

    [Fact]
    public void Merge_MechanicalSummary_ChildWins()
    {
        var parent = new ConditionDefinition { Name = "base_cond", MechanicalSummary = "parent summary" };
        var child = new ConditionDefinition { Name = "sub_cond", Inherits = ["base_cond"], MechanicalSummary = "child summary" };

        var merged = ConditionDefinition.Merge(child, parent);

        Assert.Equal("child summary", merged.MechanicalSummary);
    }

    [Fact]
    public void Merge_MechanicalSummary_ParentFillsGap()
    {
        var parent = new ConditionDefinition { Name = "base_cond", MechanicalSummary = "parent summary" };
        var child = new ConditionDefinition { Name = "sub_cond", Inherits = ["base_cond"], MechanicalSummary = null };

        var merged = ConditionDefinition.Merge(child, parent);

        Assert.Equal("parent summary", merged.MechanicalSummary);
    }

    // ── Embedded YAML loading ─────────────────────────────────────────────────

    [Fact]
    public void Provider_LoadsDnd5eConditions_FromEmbeddedResources()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);

        Assert.True(conditions.Count >= 15, $"Expected at least 15 D&D 5e conditions, got {conditions.Count}");
        Assert.True(conditions.ContainsKey("frightened"));
        Assert.True(conditions.ContainsKey("blinded"));
        Assert.True(conditions.ContainsKey("poisoned"));
    }

    [Fact]
    public void Provider_Frightened_HasCorrectDurationType()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);

        Assert.True(conditions.TryGetValue("frightened", out var def));
        Assert.NotNull(def);
        Assert.Equal(ConditionDurationType.Manual, def.DurationType);
        Assert.NotEmpty(def.MechanicalSummary!);
    }

    [Fact]
    public void Provider_Poisoned_IsTimedDuration()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);

        Assert.True(conditions.TryGetValue("poisoned", out var def));
        Assert.Equal(ConditionDurationType.Timed, def.DurationType);
    }

    [Fact]
    public void Provider_LoadsFallout2d20Conditions_FromEmbeddedResources()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Fallout2d20);

        Assert.Equal(4, conditions.Count);
        Assert.True(conditions.ContainsKey("addicted"));
        Assert.True(conditions.ContainsKey("crippled"));
        Assert.True(conditions.ContainsKey("poisoned"));
        Assert.True(conditions.ContainsKey("radiation_poisoning"));
        Assert.All(conditions.Values, def => Assert.NotEmpty(def.MechanicalSummary ?? string.Empty));
    }

    [Fact]
    public void Provider_Dnd5eExhaustion_IsStacking()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);

        Assert.True(conditions.TryGetValue("exhaustion", out var def));
        Assert.NotNull(def);
        Assert.Equal(ConditionDurationType.UntilLongRest, def.DurationType);
        Assert.True(def.IsStacking);
    }

    [Fact]
    public void Provider_Pf2eFatigued_IsNotStacking()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(conditions.TryGetValue("fatigued", out var def));
        Assert.NotNull(def);
        Assert.Equal(ConditionDurationType.UntilLongRest, def.DurationType);
        Assert.False(def.IsStacking);
    }

    [Fact]
    public void Provider_Invisible_IsConcentrationDuration()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);

        Assert.True(conditions.TryGetValue("invisible", out var def));
        Assert.Equal(ConditionDurationType.Concentration, def.DurationType);
    }

    // ── StatusExpiryRule integration ──────────────────────────────────────────

    [Fact]
    public async Task StatusExpiryRule_KnownCondition_UsesDataDrivenExpiry_TimedExpires()
    {
        // "poisoned" is Timed → ExpiresAtDay fires normally
        var rule = new StatusExpiryRule(Provider);
        var character = MakeCharacter(
            new StatusEffect { Name = "Poisoned", ConditionName = "poisoned", ExpiresAtDay = 5 });

        var result = await RunRule(rule, character, currentDay: 10);

        Assert.Single(result.Deltas);
        var remove = Assert.IsType<StatusRemove>(result.Deltas[0]);
        Assert.Equal("Poisoned", remove.Status);
    }

    [Fact]
    public async Task StatusExpiryRule_KnownCondition_UsesDataDrivenExpiry_ManualDoesNotExpire()
    {
        // "frightened" is Manual → day-based expiry should NOT fire even if ExpiresAtDay is set
        var rule = new StatusExpiryRule(Provider);
        var character = MakeCharacter(
            new StatusEffect { Name = "Frightened", ConditionName = "frightened", ExpiresAtDay = 5 });

        var result = await RunRule(rule, character, currentDay: 10);

        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task StatusExpiryRule_UnknownCondition_FallsBackToHeuristic()
    {
        // Unknown conditionName → existing ExpiresAtDay logic runs unchanged
        var rule = new StatusExpiryRule(Provider);
        var character = MakeCharacter(
            new StatusEffect { Name = "Custom Curse", ConditionName = "homebrew_custom_curse", ExpiresAtDay = 5 });

        var result = await RunRule(rule, character, currentDay: 10);

        Assert.Single(result.Deltas);
        var remove = Assert.IsType<StatusRemove>(result.Deltas[0]);
        Assert.Equal("Custom Curse", remove.Status);
    }

    [Fact]
    public async Task StatusExpiryRule_NullConditionName_FallsBackToHeuristic()
    {
        // Null conditionName (legacy effect) → ExpiresAtDay fires as before
        var rule = new StatusExpiryRule(Provider);
        var character = MakeCharacter(
            new StatusEffect { Name = "Old Wound", ConditionName = null, ExpiresAtDay = 5 });

        var result = await RunRule(rule, character, currentDay: 10);

        Assert.Single(result.Deltas);
    }

    [Fact]
    public void ShouldExpireAtDawn_UntilDawnDefinition_RequiresDayAdvance()
    {
        var effect = new StatusEffect { Name = "Veil", ConditionName = "hidden" };
        var def = new ConditionDefinition { Name = "hidden", DurationType = ConditionDurationType.UntilDawn };

        Assert.False(ConditionExpiryEvaluator.ShouldExpireAtDawn(effect, def, 0));
        Assert.True(ConditionExpiryEvaluator.ShouldExpireAtDawn(effect, def, 1));
    }

    [Fact]
    public void ShouldExpireOnLongRest_ExhaustionDefinition_ReturnsTrue()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Dnd5e);
        Assert.True(conditions.TryGetValue("exhaustion", out var def));

        var effect = new StatusEffect { Name = "Exhaustion 1", ConditionName = "exhaustion" };

        Assert.True(ConditionExpiryEvaluator.ShouldExpireOnLongRest(effect, def));
    }

    [Fact]
    public void Provider_LoadsPf2eConditions_FromEmbeddedResources()
    {
        var conditions = Provider.GetConditionsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(conditions.Count >= 15, $"Expected at least 15 PF2e conditions, got {conditions.Count}");
        Assert.True(conditions.ContainsKey("blinded"));
        Assert.True(conditions.ContainsKey("restrained"));
    }

    private static Character MakeCharacter(params StatusEffect[] effects) =>
        new()
        {
            Id = "chars/test_" + Guid.NewGuid().ToString("N"),
            Name = "Test Character",
            SystemStats = new Dnd5eExtension { StatusEffects = [.. effects] }
        };

    private static Task<RuleResult> RunRule(
        StatusExpiryRule rule,
        Character character,
        int currentDay,
        double daysPassed = 0)
    {
        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = currentDay },
            [],
            [character],
            null!,
            daysPassed,
            "test_campaign");

        return rule.ApplyAsync(context, CancellationToken.None);
    }
}
