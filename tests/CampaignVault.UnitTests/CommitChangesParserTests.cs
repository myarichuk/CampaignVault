using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class CommitChangesParserTests
{
    [Fact]
    public void TryParse_GrokWebCommitPayload_DeserializesAllChanges()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "location_update",
                                   "locationId": "locations/aurelia-golden-tavern",
                                   "ambientCrowd": "A handful of regulars nursing drinks"
                                 },
                                 {
                                   "$type": "character_update",
                                   "characterId": "chars/barkeep-thorne",
                                   "appearanceOverride": "Wiping mugs behind the bar"
                                 },
                                 {
                                   "$type": "travel",
                                   "characterId": "chars/valen",
                                   "destinationLocationId": "locations/aurelia-golden-tavern",
                                   "narrative": "Valen approaches the bar."
                                 },
                                 {
                                   "$type": "activity",
                                   "characterId": "chars/valen",
                                   "newLocationId": "locations/aurelia-golden-tavern",
                                   "newActivity": "Approaching the bar and speaking with Thorne"
                                 },
                                 {
                                   "$type": "ruleset_action",
                                   "actorId": "chars/valen",
                                   "targetIds": [],
                                   "actionType": "SkillCheck",
                                   "actionName": "Perception",
                                   "parameters": { "bonus": "1", "dc": "15" }
                                 },
                                 {
                                   "$type": "event",
                                   "category": "Conversation",
                                   "summary": "Valen greets Thorne and attempts a Perception check."
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        Assert.Equal(6, parsed!.Length);
    }

    [Fact]
    public void TryParse_EngagementRelationWithActorId_ResolvesToCharacterId()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "engagement_relation",
                                   "actorId": "chars/valen",
                                   "targetId": "chars/lirael",
                                   "category": "Social",
                                   "verb": "discussing plans with"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        var change = Assert.IsType<EngagementRelationChange>(parsed!.Single());
        Assert.Equal("chars/valen", change.CharacterId);
        Assert.Equal("chars/lirael", change.TargetId);
    }

    [Fact]
    public void TryParse_EngagementRelationWithBothActorIdAndCharacterId_PrefersCharacterId()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "engagement_relation",
                                   "actorId": "chars/wrong",
                                   "characterId": "chars/valen",
                                   "targetId": "chars/lirael",
                                   "category": "Social",
                                   "verb": "discussing plans with"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        var change = Assert.IsType<EngagementRelationChange>(parsed!.Single());
        Assert.Equal("chars/valen", change.CharacterId);
    }

    [Fact]
    public void TryParse_ItemUpdate_DeserializesStringCoreCategory()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "item_update",
                                   "itemId": "items/greataxe",
                                   "coreCategory": "Armor"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        var itemUpdate = Assert.IsType<ItemUpdate>(parsed![0]);
        Assert.Equal(ItemCategory.Armor, itemUpdate.CoreCategory);
    }

    [Fact]
    public void TryParse_RulesetAction_MissingActionType_FailsInsteadOfDefaultingToAttack()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "ruleset_action",
                                   "characterId": "chars/valen",
                                   "actionName": "Perception"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("actionType", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_QuestProgress_MissingNewState_FailsInsteadOfDefaultingToOpen()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "quest_progress",
                                   "questId": "quests/rats_01",
                                   "objectiveIndex": 0,
                                   "narrativeNote": "Rats spotted"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Contains("newState", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalize_Commit_UnwrapsStringEncodedChangesArray()
    {
        var args = new JsonObject
        {
            ["changes"] = """[{"$type":"event","category":"Conversation","summary":"test"}]""",
            ["narrative"] = "Beat narrative",
        };

        var modified = ToolCallExamples.TryNormalize("commit", args, out var rewrites);

        Assert.True(modified);
        Assert.IsType<JsonArray>(args["changes"]);
        Assert.Contains("changes(string)→changes(array)", rewrites);
    }
}
