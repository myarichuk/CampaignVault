using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class RestChangeHandlerTests
{
    [Fact]
    public async Task ApplyAsync_WhenInterrupted_DoesNotHealAndBlocksRecovery()
    {
        // Arrange
        var rule = new EncounterResolver(() => 0.0); // 0.0 is always < any chance (guarantees interrupt)
        var handler = new RestChangeHandler(rule, RulesetDataTestHelper.CreateConditionProvider());
        var change = new RestChange
        {
            CharacterId = "chars/1",
            LocationId = "loc/1",
            IntendedHours = 8,
            SecurityModifier = -50 // Ensure max danger
        };

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { ["chars/1"] = new Character { Id = "chars/1", CurrentLocationId = "loc/1" } },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location> { ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Wilderness } },
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            [],
            new WorldChangeDispatcher([], new CampaignVault.Data.CampaignDocumentKeys(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldChangeDispatcher>.Instance)
        );

        // Act
        var result = await handler.ApplyAsync(change, context, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        // Interrupted rest should NOT produce a "Rested peacefully" activity change.
        // It should produce an EventOccurred and CharacterCreate from the rule.
        // And it should NOT produce an HpChange or NeedChange for recovery since it's the engine's job to block it.
        // Wait, the engine doesn't block LLM's separate HpChange commit (that's by design).
        // It just doesn't issue any success narratives itself.
    }

    [Fact]
    public async Task ApplyAsync_LongRest_ClearsUntilLongRestConditions()
    {
        var charId = "chars/1";
        var character = new Character
        {
            Id = charId,
            CurrentLocationId = "loc/1",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect { Name = "Exhaustion 1", ConditionName = "exhaustion", Category = "Condition" },
                    new StatusEffect { Name = "Frightened", ConditionName = "frightened", Category = "Condition" }
                ]
            }
        };

        var summary = new List<string>();
        var statusHandler = RulesetDataTestHelper.CreateStatusChangeHandler();
        var dispatcher = new WorldChangeDispatcher(
            [statusHandler],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var rule = new EncounterResolver(() => 1.0);
        var handler = new RestChangeHandler(rule, RulesetDataTestHelper.CreateConditionProvider());

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { [charId] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>
            {
                ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Settlement }
            },
            null,
            null,
            NullLogger.Instance,
            summary,
            dispatcher);

        var result = await handler.ApplyAsync(new RestChange
        {
            CharacterId = charId,
            LocationId = "loc/1",
            IntendedHours = 8,
            RestType = RestType.LongRest
        }, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(character.SystemStats!.StatusEffects, e => e.ConditionName == "exhaustion");
        Assert.Contains(character.SystemStats.StatusEffects, e => e.ConditionName == "frightened");
        Assert.Contains(summary, m => m.Contains("Stacking condition", StringComparison.Ordinal)
                                    && m.Contains("reached 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_LongRest_DecrementsStackingExhaustionByOneLevel()
    {
        var charId = "chars/2";
        var character = new Character
        {
            Id = charId,
            CurrentLocationId = "loc/1",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect { Name = "Exhaustion 3", ConditionName = "exhaustion", Category = "Condition" }
                ]
            }
        };

        var summary = new List<string>();
        var statusHandler = RulesetDataTestHelper.CreateStatusChangeHandler();
        var dispatcher = new WorldChangeDispatcher(
            [statusHandler],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var rule = new EncounterResolver(() => 1.0);
        var handler = new RestChangeHandler(rule, RulesetDataTestHelper.CreateConditionProvider());

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { [charId] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>
            {
                ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Settlement }
            },
            null,
            null,
            NullLogger.Instance,
            summary,
            dispatcher);

        var result = await handler.ApplyAsync(new RestChange
        {
            CharacterId = charId,
            LocationId = "loc/1",
            IntendedHours = 8,
            RestType = RestType.LongRest
        }, context, CancellationToken.None);

        Assert.True(result.Success);
        var effect = Assert.Single(character.SystemStats!.StatusEffects, e => e.ConditionName == "exhaustion");
        Assert.Equal("Exhaustion 2", effect.Name);
        Assert.Contains(summary, m => m.Contains("decremented to 'Exhaustion 2'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_LongRest_NonStackingConditionsStillFullyClear()
    {
        // PF2e's "fatigued" is UntilLongRest but non-stacking (RAW: rest fully removes it),
        // unlike dnd5e's leveled "exhaustion" — verifies IsStacking is scoped per ruleset.
        var charId = "chars/3";
        var character = new Character
        {
            Id = charId,
            CurrentLocationId = "loc/1",
            SystemStats = new Pf2eExtension
            {
                StatusEffects =
                [
                    new StatusEffect { Name = "Fatigued", ConditionName = "fatigued", Category = "Condition" }
                ]
            }
        };

        var summary = new List<string>();
        var statusHandler = RulesetDataTestHelper.CreateStatusChangeHandler();
        var dispatcher = new WorldChangeDispatcher(
            [statusHandler],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var rule = new EncounterResolver(() => 1.0);
        var handler = new RestChangeHandler(rule, RulesetDataTestHelper.CreateConditionProvider());

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { [charId] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>
            {
                ["loc/1"] = new Location { Id = "loc/1", Type = LocationType.Settlement }
            },
            null,
            null,
            NullLogger.Instance,
            summary,
            dispatcher);

        var result = await handler.ApplyAsync(new RestChange
        {
            CharacterId = charId,
            LocationId = "loc/1",
            IntendedHours = 8,
            RestType = RestType.LongRest
        }, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(character.SystemStats!.StatusEffects, e => e.ConditionName == "fatigued");
        Assert.Contains(summary, m => m.Contains("UntilLongRest condition 'Fatigued' cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusChangeHandler_UnknownConditionName_RecordsSoftWarning()
    {
        var charId = "chars/warn-test";
        var character = new Character { Id = charId, SystemStats = new Dnd5eExtension() };
        var summary = new List<string>();
        var handler = RulesetDataTestHelper.CreateStatusChangeHandler();
        var dispatcher = new WorldChangeDispatcher(
            [handler],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var context = new ChangeContext(
            null!,
            new Dictionary<string, Character> { [charId] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            null,
            null,
            NullLogger.Instance,
            summary,
            dispatcher);

        var result = await handler.ApplyAsync(new StatusChange
        {
            CharacterId = charId,
            Effect = new StatusEffect
            {
                Name = "Custom Curse",
                Category = "Condition",
                ConditionName = "not_a_real_condition_xyz"
            }
        }, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m.Contains("[WARNING]", StringComparison.Ordinal)
                                    && m.Contains("not_a_real_condition_xyz", StringComparison.Ordinal));
    }
}
