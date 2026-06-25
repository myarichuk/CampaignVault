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
                                   "$type": "location_create",
                                   "locationId": "locations/aurelia",
                                   "name": "Aurelia, the City of Gold",
                                   "description": "A sprawling marketplace city.",
                                   "type": "Settlement"
                                 },
                                 {
                                   "$type": "location_create",
                                   "locationId": "locations/aurelia-golden-tavern",
                                   "name": "The Gilded Rose",
                                   "description": "A renowned tavern.",
                                   "type": "Building",
                                   "connectedFromLocationId": "locations/aurelia",
                                   "connectionDescription": "Oak door with golden rose emblem."
                                 },
                                 {
                                   "$type": "character_create",
                                   "characterId": "chars/barkeep-thorne",
                                   "name": "Thorne Ironkeg",
                                   "currentLocationId": "locations/aurelia-golden-tavern",
                                   "currentActivity": "Wiping mugs behind the bar",
                                   "keepAlive": false
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
        Assert.Equal(7, parsed!.Length);
    }

    [Fact]
    public void TryParse_RumorCreate_Deserializes()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "rumor_create",
                                   "rumorId": "rumors/nightshade-gang",
                                   "subject": "Nightshade Gang",
                                   "text": "Pirates raided three barges."
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        var rumorCreate = Assert.IsType<RumorCreate>(parsed!.Single());
        Assert.Equal("rumors/nightshade-gang", rumorCreate.RumorId);
        Assert.Equal("Nightshade Gang", rumorCreate.Subject);
        Assert.Equal("Pirates raided three barges.", rumorCreate.Text);
    }

    [Fact]
    public void TryParse_ItemCreate_DeserializesStringCoreCategory()
    {
        const string payload = """
                               [
                                 {
                                   "$type": "item_create",
                                   "itemId": "items/greataxe",
                                   "name": "Greataxe",
                                   "description": "A heavy two-handed axe.",
                                   "holderId": "characters/kergil",
                                   "coreCategory": "Weapon"
                                 }
                               ]
                               """;

        using var doc = JsonDocument.Parse(payload);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        var itemCreate = Assert.IsType<ItemCreate>(parsed![0]);
        Assert.Equal(ItemCategory.Weapon, itemCreate.CoreCategory);
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