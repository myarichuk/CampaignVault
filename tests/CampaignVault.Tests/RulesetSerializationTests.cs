using CampaignVault.Models;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetSerializationTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void RulesetAction_PolymorphicSerialization_WorksCorrectly()
    {
        // Arrange: Prepare JSON payload for RulesetAction
        var json = """
        [
          {
            "$type": "ruleset_action",
            "actorId": "characters/grog",
            "targetIds": ["characters/elara-voss"],
            "actionName": "longsword",
            "actionType": "Attack",
            "actionCategory": "Melee",
            "parameters": {
              "advantage": "true",
              "dc": "15"
            }
          }
        ]
        """;

        // Act: Deserialize
        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, _options);

        // Assert
        Assert.NotNull(changes);
        Assert.Single(changes);
        Assert.IsType<RulesetAction>(changes[0]);

        var action = (RulesetAction)changes[0];
        Assert.Equal("characters/grog", action.ActorId);
        Assert.Single(action.TargetIds);
        Assert.Equal("characters/elara-voss", action.TargetIds[0]);
        Assert.Equal("longsword", action.ActionName);
        Assert.Equal(RulesetActionType.Attack, action.ActionType);
        Assert.Equal(ActionCategory.Melee, action.ActionCategory);
        Assert.Equal("true", action.Parameters["advantage"]);
        Assert.Equal("15", action.Parameters["dc"]);

        // Act: Serialize back and verify structure
        var serialized = JsonSerializer.Serialize<WorldChange>(action, _options);
        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("$type", out var typeProp) && typeProp.GetString() == "ruleset_action");
        Assert.True(root.TryGetProperty("actorId", out var actorProp) && actorProp.GetString() == "characters/grog");
        Assert.True(root.TryGetProperty("actionType", out var actionTypeProp) && actionTypeProp.GetString() == "Attack");
        Assert.True(root.TryGetProperty("actionCategory", out var categoryProp) && categoryProp.GetString() == "Melee");
    }

    [Fact]
    public void StatusChange_WithStructuredEffect_PolymorphicSerialization_WorksCorrectly()
    {
        // Arrange: Prepare JSON payload for StatusChange with StatusEffect
        var json = """
        [
          {
            "$type": "status",
            "characterId": "characters/grog",
            "effect": {
              "name": "Mangled Left Hand",
              "category": "Injury",
              "affectedPart": "LeftHand",
              "statModifiers": {
                "AttackRoll": -2.0,
                "Speed": -5.0
              },
              "expiresAtDay": 14.5,
              "expiresAtRound": null,
              "recoveryHint": "Requires Medicine DC 15 check or healing spell.",
              "appliedBy": "npcs/enemy-wizard"
            }
          }
        ]
        """;

        // Act: Deserialize
        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, _options);

        // Assert
        Assert.NotNull(changes);
        Assert.Single(changes);
        Assert.IsType<StatusChange>(changes[0]);

        var statusChange = (StatusChange)changes[0];
        Assert.Equal("characters/grog", statusChange.CharacterId);
        Assert.NotNull(statusChange.Effect);

        var effect = statusChange.Effect;
        Assert.Equal("Mangled Left Hand", effect.Name);
        Assert.Equal("Injury", effect.Category);
        Assert.Equal(BodyPart.LeftHand, effect.AffectedPart);
        Assert.Equal(-2.0f, effect.StatModifiers["AttackRoll"]);
        Assert.Equal(-5.0f, effect.StatModifiers["Speed"]);
        Assert.Equal(14.5f, effect.ExpiresAtDay);
        Assert.Null(effect.ExpiresAtRound);
        Assert.Equal("Requires Medicine DC 15 check or healing spell.", effect.RecoveryHint);
        Assert.Equal("npcs/enemy-wizard", effect.AppliedBy);

        // Act: Serialize back and verify structure
        var serialized = JsonSerializer.Serialize<WorldChange>(statusChange, _options);
        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("$type", out var typeProp) && typeProp.GetString() == "status");
        Assert.True(root.TryGetProperty("effect", out var effectProp));
        Assert.True(effectProp.TryGetProperty("affectedPart", out var partProp) && partProp.GetString() == "LeftHand");
        Assert.True(effectProp.TryGetProperty("statModifiers", out var modifiersProp));
        Assert.Equal(-2.0f, modifiersProp.GetProperty("AttackRoll").GetSingle());
    }

    [Fact]
    public void StatusChange_LegacyStringFallback_PolymorphicSerialization_WorksCorrectly()
    {
        // Arrange
        var json = """
        [
          {
            "$type": "status",
            "characterId": "characters/grog",
            "status": "Frightened"
          }
        ]
        """;

        // Act
        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, _options);

        // Assert
        Assert.NotNull(changes);
        Assert.Single(changes);
        Assert.IsType<StatusChange>(changes[0]);

        var statusChange = (StatusChange)changes[0];
        Assert.Equal("characters/grog", statusChange.CharacterId);
        Assert.Null(statusChange.Effect);
        Assert.Equal("Frightened", statusChange.Status);
    }

    [Theory]
    [InlineData("[]", 0)] // Empty array
    [InlineData("[{ \"$type\": \"hp\", \"characterId\": \"hero\", \"delta\": 5 }]", 1)] // Valid
    public void WorldChange_Deserialization_HandlesEmptyAndValid(string json, int expectedCount)
    {
        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, _options);
        
        Assert.NotNull(changes);
        Assert.Equal(expectedCount, changes.Length);
    }

    [Theory]
    [InlineData("Not JSON")]
    [InlineData("[{ \"$type\": \"hp_change\", \"characterId\": \"hero\", \"delta\": 5 }]")] // Wrong discriminator (should be "hp")
    [InlineData("[{ \"$type\": \"unknown_change_type\", \"someField\": \"value\" }]")] // Unknown discriminator
    [InlineData("[{ \"$type\": \"hp\", \"delta\": \"NOT_A_NUMBER\" }]")] // Type mismatch for integer
    [InlineData("{ \"$type\": \"hp\" }")] // Object instead of Array
    public void WorldChange_Deserialization_MalformedJson_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorldChange[]>(json, _options));
    }
}
