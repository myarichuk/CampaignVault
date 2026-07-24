using System;
using System.Text.Json.Nodes;
using CampaignVault.Middleware;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class ToolCallExamplesTests
{
    [Theory]
    [InlineData("get_entity", "characterId", "entityId", "chars/durnan")]
    [InlineData("get_entity", "npcId", "entityId", "chars/durnan")]
    [InlineData("get_entity", "locationId", "entityId", "locations/tavern")]
    [InlineData("get_entity", "id", "entityId", "quests/rats_01")]
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
    public void TryNormalize_TakeTurn_RewritesWorldChangesAliasAndWrapsIntoRequest()
    {
        var args = new JsonObject { ["worldChanges"] = "[{\"$type\":\"event\"}]" };

        var modified = ToolCallExamples.TryNormalize("take_turn", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("worldChanges→changes", rewrites);
        Assert.Contains("flattened→request", rewrites);
        Assert.True(args.ContainsKey("request"));
        Assert.True(args["request"]!.AsObject().ContainsKey("changes"));
    }

    [Fact]
    public void TryNormalize_Combat_RewritesCombatantsToCombatantIds()
    {
        var args = new JsonObject
        {
            ["action"] = "start",
            ["locationId"] = "locations/harluaa/training-hall",
            ["combatants"] = new JsonArray { "chars/valen", "chars/harluaa-training-sergeant" },
        };

        var modified = ToolCallExamples.TryNormalize("combat", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("combatants→combatantIds", rewrites);
        Assert.True(args.ContainsKey("combatantIds"));
        Assert.False(args.ContainsKey("combatants"));
        Assert.Equal("chars/valen", args["combatantIds"]![0]!.GetValue<string>());
        Assert.Equal("chars/harluaa-training-sergeant", args["combatantIds"]![1]!.GetValue<string>());
    }

    [Fact]
    public void TryNormalize_TakeTurn_RewritesSpellParameterAliases()
    {
        // Flattened call (no 'request' wrapper) — the normalizer wraps it AND fixes nested aliases.
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "ruleset_action",
                    ["actorId"] = "chars/wizard",
                    ["actionType"] = "Spell",
                    ["actionName"] = "Fireball",
                    ["parameters"] = new JsonObject
                    {
                        ["spellResolution"] = "save",
                        ["half_on_save"] = "true",
                    },
                },
            },
        };

        var modified = ToolCallExamples.TryNormalize("take_turn", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("flattened→request", rewrites);
        Assert.Contains("ruleset_action.spellResolution→resolution", rewrites);
        var parameters = args["request"]!["changes"]![0]!["parameters"]!.AsObject();
        Assert.Equal("save", parameters["resolution"]!.GetValue<string>());
        Assert.Equal("true", parameters["halfOnSave"]!.GetValue<string>());
    }

    [Fact]
    public void TryNormalize_TakeTurn_RewritesEventParticipantsToInvolved()
    {
        var args = new JsonObject
        {
            ["request"] = new JsonObject
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
            },
        };

        var modified = ToolCallExamples.TryNormalize("take_turn", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("event.participants→involved", rewrites);
        var change = args["request"]!["changes"]![0]!.AsObject();
        Assert.True(change.ContainsKey("involved"));
        Assert.False(change.ContainsKey("participants"));
        Assert.Equal("chars/valen", change["involved"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("get_entity", "entityId")]
    [InlineData("take_turn", "request")]
    public void BuildMissingParamResponse_IncludesRetryExample(string tool, string param)
    {
        var (summary, retryExample) = ToolCallExamples.BuildMissingParamResponse(tool, param, "guidance here");

        Assert.Contains(param, summary);
        Assert.Contains("tools/call", summary);
        Assert.NotNull(retryExample);
        Assert.Equal(tool, retryExample!.Value.GetProperty("params").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("get_entity", "entityId", "tools/call")]
    [InlineData("take_turn", "request", "event")]
    public void McpToolErrorFilter_BuildMissingParamMessage_IncludesRetryForRegisteredTools(
        string tool, string param, string expectedFragment)
    {
        var message = McpToolErrorFilter.BuildMissingParamMessage(tool, param);

        Assert.Contains(expectedFragment, message);
        Assert.Contains(param, message);
    }
}
