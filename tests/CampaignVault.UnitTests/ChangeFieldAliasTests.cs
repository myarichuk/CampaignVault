using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Per-$type field aliases, the systemStats "$system" fill-in, and the hints for what neither repairs.</summary>
public class ChangeFieldAliasTests
{
    private static JsonObject TakeTurn(params JsonObject[] changes) => new()
    {
        ["campaignName"] = "scratch",
        ["request"] = new JsonObject { ["narrative"] = "n", ["changes"] = new JsonArray(changes) },
    };

    private static JsonObject FirstChange(JsonObject args) => args["request"]!["changes"]![0]!.AsObject();

    [Fact]
    public void EveryAlias_TargetsARealField_AndIsNotOneItself()
    {
        foreach (var (type, aliases) in ToolCallExamples.ChangeFieldAliases)
        {
            var fields = CommitSchemaModel.Find(type)!.Fields.Select(f => f.JsonName).ToHashSet();
            foreach (var (canonical, names) in aliases)
            {
                Assert.Contains(canonical, fields);
                Assert.All(names, n => Assert.DoesNotContain(n, fields));
            }
        }
    }

    [Theory]
    [InlineData("activity", "activity", "newActivity")]
    [InlineData("knowledge_update", "content", "details")]
    [InlineData("resource", "resource", "poolName")]
    public void TakeTurn_RewritesAlias(string type, string alias, string canonical)
    {
        var args = TakeTurn(new JsonObject { ["$type"] = type, ["characterId"] = "chars/maeve", [alias] = "x" });

        Assert.True(ToolCallExamples.TryNormalize("take_turn", args, out var rewrites));

        var change = FirstChange(args);
        Assert.Equal("x", change[canonical]!.GetValue<string>());
        Assert.False(change.ContainsKey(alias));
        Assert.Contains($"{type}.{alias}→{canonical}", rewrites);
    }

    [Fact]
    public void SingularAliasOfListField_IsWrappedInArray()
    {
        var args = TakeTurn(new JsonObject
        {
            ["$type"] = "knowledge_update", ["characterId"] = "chars/maeve", ["topic"] = "t", ["eventId"] = "evt-1",
        });

        ToolCallExamples.TryNormalize("take_turn", args, out _);

        Assert.Equal("[\"evt-1\"]", FirstChange(args)["sourceEventIds"]!.ToJsonString());
    }

    [Fact]
    public void AliasAndCanonicalBothSent_AliasIsLeftForTheSweepToReport()
    {
        var args = TakeTurn(new JsonObject
        {
            ["$type"] = "activity", ["characterId"] = "chars/maeve", ["newActivity"] = "a", ["activity"] = "b",
        });

        ToolCallExamples.TryNormalize("take_turn", args, out _);

        Assert.True(CommitChangesParser.TryParse(
            JsonSerializer.SerializeToElement(args["request"]!["changes"]), out _, out var error) is false);
        Assert.Contains("Did you mean 'newActivity'", error);
    }

    [Fact]
    public void UnmarkedSystemStats_GetsCampaignSystemFirst_AndBinds()
    {
        var args = TakeTurn(new JsonObject
        {
            ["$type"] = "character_update",
            ["characterId"] = "chars/maeve",
            ["systemStats"] = new JsonObject
            {
                ["classLevels"] = new JsonArray(new JsonObject { ["class"] = "Wizard", ["level"] = 6 }),
            },
        });

        var unmarked = ToolCallExamples.SystemStatsWithoutMarker("take_turn", args);
        var rewrites = new System.Collections.Generic.List<string>();
        ToolCallExamples.ApplySystemMarker(unmarked, RulesetSystem.Dnd5e, rewrites);

        var stats = FirstChange(args)["systemStats"]!.AsObject();
        Assert.Equal("$system", stats.First().Key);
        Assert.True(CommitChangesParser.TryParse(
            JsonSerializer.SerializeToElement(args["request"]!["changes"]), out var parsed, out var error), error);
        var update = Assert.IsType<CharacterUpdate>(Assert.Single(parsed!));
        var dnd = Assert.IsType<Dnd5eExtension>(update.SystemStats);
        Assert.Equal("Wizard", Assert.Single(dnd.ClassLevels).Class);
    }

    [Fact]
    public void MarkedSystemStats_AndOtherTools_AreLeftAlone()
    {
        var args = TakeTurn(new JsonObject
        {
            ["$type"] = "character_update",
            ["characterId"] = "chars/maeve",
            ["systemStats"] = new JsonObject { ["$system"] = "dnd5e", ["level"] = 6 },
        });

        Assert.Empty(ToolCallExamples.SystemStatsWithoutMarker("take_turn", args));
        Assert.Empty(ToolCallExamples.SystemStatsWithoutMarker("get_entity", args));
    }

    [Fact]
    public void UnmarkedSystemStats_WhenSystemUnknown_ErrorNamesTheMarker()
    {
        var changes = JsonSerializer.SerializeToElement(new JsonArray(new JsonObject
        {
            ["$type"] = "character_update",
            ["characterId"] = "chars/maeve",
            ["systemStats"] = new JsonObject { ["classLevels"] = new JsonArray() },
        }));

        Assert.False(CommitChangesParser.TryParse(changes, out _, out var error));
        Assert.Contains("\"$system\": \"dnd5e\"", error);
        Assert.DoesNotContain("Fields of character_update", error);
    }
}
