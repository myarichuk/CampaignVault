using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Guards LLM-facing documentation and normalization against schema drift.
/// </summary>
public class LlmToolingRegressionTests
{
    [Fact]
    public void CommitEnumCheatSheet_DoesNotAdvertiseMetaActionType()
    {
        Assert.DoesNotContain(", Meta", CommitEnumCheatSheet.Compact);
        Assert.DoesNotContain("SavingThrow, Meta", CommitEnumCheatSheet.Full);
    }

    [Fact]
    public void CommitEnumCheatSheet_IncludesRumorCreateAndHpGuidance()
    {
        Assert.Contains("rumor_create", CommitEnumCheatSheet.Compact);
        Assert.Contains("rumor_create", CommitEnumCheatSheet.Full);
        Assert.Contains("duplicate `hp`", CommitEnumCheatSheet.Compact);
        Assert.Contains("SkillCheck", CommitEnumCheatSheet.Compact);
    }

    [Fact]
    public void CommitTypesReference_IncludesRumorCreate()
    {
        Assert.Contains("rumor_create", CommitTypesReference.SupportedTypesList);
    }

    [Theory]
    [InlineData(CommitRumorHelpExamples.RumorCreate, typeof(RumorCreate))]
    [InlineData(CommitRumorHelpExamples.RumorEvolve, typeof(RumorEvolves))]
    [InlineData(CommitRumorHelpExamples.QuestCreateHook, typeof(QuestCreate))]
    public void DocumentedCommitExamples_ParseSuccessfully(string json, Type expectedType)
    {
        var wrapped = $"[{json}]";
        using var doc = JsonDocument.Parse(wrapped);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.IsType(expectedType, parsed![0]);
    }

    [Fact]
    public void CommitSpellHelpExamples_IncludesConcentrationAndHpGuidance()
    {
        Assert.Contains("Concentration", CommitSpellHelpExamples.HelpSection);
        Assert.Contains("Do NOT also commit", CommitSpellHelpExamples.RoutingGuide);
        Assert.Contains("SkillCheck", CommitSpellHelpExamples.RoutingGuide);
    }

    [Fact]
    public void TryNormalize_LegacyRumorCreate_RewritesToRumorCreate()
    {
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "rumor",
                    ["subject"] = "Nightshade Gang",
                    ["newText"] = "Pirates raided barges.",
                    ["newState"] = "Active",
                    ["sourceCharacterId"] = "chars/bram-the-barkeep",
                },
            },
        };

        var modified = ToolCallExamples.TryNormalize("commit", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("rumor→rumor_create", rewrites);
        Assert.Contains("rumor.newText→text", rewrites);
        Assert.Contains("rumor.newState(Active)→removed", rewrites);

        var change = args["changes"]![0]!.AsObject();
        Assert.Equal("rumor_create", change["$type"]!.GetValue<string>());
        Assert.Equal("rumors/nightshade-gang", change["rumorId"]!.GetValue<string>());
        Assert.Equal("Pirates raided barges.", change["text"]!.GetValue<string>());
        Assert.False(change.ContainsKey("newText"));
        Assert.False(change.ContainsKey("newState"));
        Assert.False(change.ContainsKey("sourceCharacterId"));

        using var doc = JsonDocument.Parse(args["changes"]!.ToJsonString());
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);
        Assert.True(ok, error);
        var rumorCreate = Assert.IsType<RumorCreate>(parsed![0]);
        Assert.Equal("rumors/nightshade-gang", rumorCreate.RumorId);
        Assert.Equal("Nightshade Gang", rumorCreate.Subject);
    }

    [Fact]
    public void TryNormalize_LegacyRumorEvolve_KeepsRumorTypeWithRumorId()
    {
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "rumor",
                    ["subject"] = "Nightshade Gang",
                    ["newText"] = "Gang smashed.",
                    ["newState"] = "Resolved",
                },
            },
        };

        ToolCallExamples.TryNormalize("commit", args, out _);

        var change = args["changes"]![0]!.AsObject();
        Assert.Equal("rumor", change["$type"]!.GetValue<string>());
        Assert.Equal("rumors/nightshade-gang", change["rumorId"]!.GetValue<string>());
        Assert.Equal("Resolved", change["newState"]!.GetValue<string>());
        Assert.Equal("Gang smashed.", change["newText"]!.GetValue<string>());
        Assert.False(change.ContainsKey("subject"));
    }

    [Fact]
    public void TryNormalize_QuestCreate_FixesDeadlineDaysAndObjectiveState()
    {
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "quest_create",
                    ["questId"] = "quests/test",
                    ["title"] = "Test Quest",
                    ["deadlineDays"] = 14,
                    ["objectives"] = new JsonArray
                    {
                        new JsonObject { ["description"] = "Step 1", ["state"] = "Active" },
                    },
                },
            },
        };

        var modified = ToolCallExamples.TryNormalize("commit", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("quest_create.deadlineDays→deadlineDay", rewrites);

        var change = args["changes"]![0]!.AsObject();
        Assert.Equal(14, change["deadlineDay"]!.GetValue<int>());
        Assert.False(change.ContainsKey("deadlineDays"));
        Assert.False(change["objectives"]![0]!.AsObject().ContainsKey("state"));
    }

    [Fact]
    public void CommitJsonErrorHints_SuggestsNascentForActiveRumorState()
    {
        var ex = new JsonException(
            "The JSON value could not be converted to CampaignVault.Models.RumorState. Path: $[0].newState");
        using var doc = JsonDocument.Parse("""[{ "$type": "rumor", "rumorId": "rumors/x", "newState": "Active" }]""");

        var enriched = CommitJsonErrorHints.Enrich(ex, doc.RootElement);

        Assert.Contains("Nascent", enriched);
        Assert.Contains("Did you mean 'Nascent'?", enriched);
    }

    [Fact]
    public void RecommendedSystemPrompt_Under12kCharacters()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "recommended-system-prompt.md");
        var content = File.ReadAllText(path);
        var start = content.IndexOf("```text", StringComparison.Ordinal);
        var end = content.IndexOf("```", start + 7, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var prompt = content[(start + 7)..end].Trim();
        Assert.InRange(prompt.Length, 1, 12_000);
        Assert.Contains("rumor_create", prompt);
        Assert.Contains("do NOT also commit", prompt, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CampaignVault.sln"))
                || File.Exists(Path.Combine(dir, "src", "CampaignVault", "CampaignVault.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}