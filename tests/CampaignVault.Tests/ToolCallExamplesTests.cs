using System.Text.Json.Nodes;
using CampaignVault.Middleware;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class ToolCallExamplesTests
{
    [Theory]
    [InlineData("get_npc_context", "npcId", "characterId", "chars/durnan")]
    [InlineData("get_npc_context", "charId", "characterId", "chars/durnan")]
    [InlineData("get_npc_needs", "npc_id", "characterId", "characters/innkeeper")]
    [InlineData("get_scene", "locId", "locationId", "locations/tavern")]
    [InlineData("commit", "worldChanges", "changes", "[{\"$type\":\"event\"}]")]
    public void TryNormalize_RewritesKnownSynonyms(string tool, string wrongKey, string canonicalKey, string value)
    {
        var args = new JsonObject { [wrongKey] = value };

        var modified = ToolCallExamples.TryNormalize(tool, args, out var rewrites);

        Assert.True(modified);
        Assert.Contains(args[canonicalKey]!.ToString(),
            value.Trim('"').Length > 0 ? args[canonicalKey]!.ToString() : value);
        Assert.False(args.ContainsKey(wrongKey));
        Assert.Contains($"{wrongKey}→{canonicalKey}", rewrites);
    }

    [Fact]
    public void TryNormalize_Commit_RewritesEventParticipantsToInvolved()
    {
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "event",
                    ["category"] = "Conversation",
                    ["summary"] = "Valen spoke with Lirael.",
                    ["participants"] = new JsonArray { "chars/valen", "chars/lirael-goldvein" },
                },
            },
            ["narrative"] = "Bar conversation.",
        };

        var modified = ToolCallExamples.TryNormalize("commit", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("event.participants→involved", rewrites);
        var change = args["changes"]![0]!.AsObject();
        Assert.True(change.ContainsKey("involved"));
        Assert.False(change.ContainsKey("participants"));
        Assert.Equal("chars/valen", change["involved"]![0]!.GetValue<string>());
    }

    [Fact]
    public void TryNormalize_UpsertLocation_RenamesLegacyLKey()
    {
        var args = new JsonObject
        {
            ["l"] = new JsonObject
            {
                ["id"] = "locations/tavern",
                ["name"] = "Tavern",
                ["type"] = "Building",
            },
        };

        var modified = ToolCallExamples.TryNormalize("upsert_location", args, out var rewrites);

        Assert.True(modified);
        Assert.True(args.ContainsKey("location"));
        Assert.False(args.ContainsKey("l"));
        Assert.Contains("l→location", rewrites);
    }

    [Fact]
    public void TryNormalize_UpsertLocation_WrapsFlattenedPayload()
    {
        var args = new JsonObject
        {
            ["id"] = "locations/tavern",
            ["name"] = "Tavern",
            ["type"] = "Building",
        };

        var modified = ToolCallExamples.TryNormalize("upsert_location", args, out var rewrites);

        Assert.True(modified);
        Assert.True(args.ContainsKey("location"));
        Assert.Equal("locations/tavern", args["location"]!["id"]!.GetValue<string>());
        Assert.Contains("flattened→location", rewrites);
    }

    [Theory]
    [InlineData("get_npc_context", "characterId")]
    [InlineData("get_scene", "locationId")]
    [InlineData("commit", "changes")]
    [InlineData("upsert_location", "location")]
    public void BuildMissingParamResponse_IncludesRetryExample(string tool, string param)
    {
        var (summary, retryExample) = ToolCallExamples.BuildMissingParamResponse(tool, param, "guidance here");

        Assert.Contains(param, summary);
        Assert.Contains("tools/call", summary);
        Assert.NotNull(retryExample);
        Assert.Equal(tool, retryExample!.Value.GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public void BuildDeserializationErrorResponse_ForUpsertLocation_MentionsValidTypes()
    {
        var (summary, retryExample) = ToolCallExamples.BuildDeserializationErrorResponse(
            "upsert_location",
            "Could not convert to LocationType");

        Assert.Contains("Building", summary);
        Assert.Contains("Wilderness", summary);
        Assert.NotNull(retryExample);
    }

    [Theory]
    [InlineData("get_npc_context", "characterId", "tools/call")]
    [InlineData("commit", "changes", "event")]
    public void McpToolErrorFilter_BuildMissingParamMessage_IncludesRetryForRegisteredTools(
        string tool, string param, string expectedFragment)
    {
        var message = McpToolErrorFilter.BuildMissingParamMessage(tool, param);

        Assert.Contains(expectedFragment, message);
        Assert.Contains(param, message);
    }
}