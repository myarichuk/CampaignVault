using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;
using static CampaignVault.Tools.ToolCatalog;

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
    public void CommitEnumCheatSheet_IncludesRumorEvolveAndHpGuidance()
    {
        Assert.Contains("world_build", CommitEnumCheatSheet.Compact);
        Assert.Contains("world_build", CommitEnumCheatSheet.Full);
        Assert.Contains("duplicate `hp`", CommitEnumCheatSheet.Compact);
        Assert.Contains("SkillCheck", CommitEnumCheatSheet.Compact);
    }

    [Fact]
    public void CommitTypesReference_DoesNotAdvertiseRemovedCreateDiscriminators()
    {
        Assert.DoesNotContain("rumor_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("quest_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("faction_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("location_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("item_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("plot_thread_create", CommitTypesReference.SupportedTypesList);
        Assert.DoesNotContain("character_create", CommitTypesReference.SupportedTypesList);
    }

    [Fact]
    public void ArchiveEntity_IsListedAsSupportedType()
    {
        // Regression guard for the exact gap found with character_create: a $type that exists as a
        // C# WorldChange but has no [JsonDerivedType] mapping is silently unreachable from commit.
        Assert.Contains("archive_entity", CommitTypesReference.SupportedTypesList);
    }

    [Fact]
    public void ArchiveEntity_RawJson_DeserializesToArchiveEntityChange()
    {
        const string json = """[{"$type":"archive_entity","entityType":"Quest","entityId":"quests/stop-nightshade","archived":true}]""";
        using var doc = JsonDocument.Parse(json);
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.NotNull(parsed);
        var change = Assert.IsType<ArchiveEntityChange>(Assert.Single(parsed!));
        Assert.Equal(ArchivableEntityType.Quest, change.EntityType);
        Assert.Equal("quests/stop-nightshade", change.EntityId);
        Assert.True(change.Archived);
    }

    [Theory]
    [InlineData(CommitRumorHelpExamples.RumorEvolve, typeof(RumorEvolves))]
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
    public void TryNormalize_RumorEvolve_FixesActiveStateTypoAndStripsLegacyField()
    {
        var args = new JsonObject
        {
            ["changes"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "rumor",
                    ["rumorId"] = "rumors/nightshade-gang",
                    ["newText"] = "Pirates raided barges.",
                    ["newState"] = "Active",
                    ["sourceCharacterId"] = "chars/bram-the-barkeep",
                },
            },
        };

        var modified = ToolCallExamples.TryNormalize("commit", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("rumor.newState(Active)→Nascent", rewrites);
        Assert.Contains("rumor.removed sourceCharacterId", rewrites);

        var change = args["changes"]![0]!.AsObject();
        Assert.Equal("rumor", change["$type"]!.GetValue<string>());
        Assert.Equal("Nascent", change["newState"]!.GetValue<string>());
        Assert.False(change.ContainsKey("sourceCharacterId"));

        using var doc = JsonDocument.Parse(args["changes"]!.ToJsonString());
        var ok = CommitChangesParser.TryParse(doc.RootElement, out var parsed, out var error);
        Assert.True(ok, error);
        var rumorEvolve = Assert.IsType<RumorEvolves>(parsed![0]);
        Assert.Equal("rumors/nightshade-gang", rumorEvolve.RumorId);
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
    public void ModelEnumErrorHints_SuggestsNascentForActiveRumorState()
    {
        var ex = new JsonException(
            "The JSON value could not be converted to CampaignVault.Models.RumorState. Path: $[0].newState");
        using var doc = JsonDocument.Parse("""[{ "$type": "rumor", "rumorId": "rumors/x", "newState": "Active" }]""");

        var enriched = ModelEnumErrorHints.Enrich(ex, doc.RootElement);

        Assert.Contains("Nascent", enriched);
        Assert.Contains("Did you mean 'Nascent'?", enriched);
    }

    [Fact]
    public void TryNormalize_FlattenedUpsertLocation_WrapsIntoLocationKey()
    {
        var args = new JsonObject
        {
            ["id"] = "locations/rusty-nail",
            ["name"] = "The Rusty Nail",
            ["description"] = "A dim tavern near the docks.",
            ["type"] = "Building",
            ["campaignName"] = "dragon-heist",
        };

        var modified = ToolCallExamples.TryNormalize("upsert_location", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("flattened→location", rewrites);
        Assert.True(args.ContainsKey("location"));
        var location = args["location"]!.AsObject();
        Assert.Equal("locations/rusty-nail", location["id"]!.GetValue<string>());
        Assert.Equal("The Rusty Nail", location["name"]!.GetValue<string>());
        // campaignName is a sibling parameter of the tool, not part of the location payload,
        // but the flattening repair wraps the whole top-level object — the handler still finds
        // campaignName inside the wrapper only if it was present at the root before wrapping.
        Assert.Equal("dragon-heist", args["campaignName"]!.GetValue<string>());
    }

    [Fact]
    public void TryNormalize_ProperlyWrappedUpsertCharacter_IsLeftUnmodified()
    {
        var args = new JsonObject
        {
            ["character"] = new JsonObject { ["id"] = "chars/valen", ["name"] = "Valen" },
            ["campaignName"] = "dragon-heist",
        };

        var modified = ToolCallExamples.TryNormalize("upsert_character", args, out var rewrites);

        Assert.False(modified);
        Assert.Empty(rewrites);
        Assert.True(args.ContainsKey("character"));
    }

    [Theory]
    [InlineData("upsert_character")]
    [InlineData("upsert_location")]
    [InlineData("upsert_item")]
    [InlineData("upsert_creature")]
    [InlineData("upsert_faction")]
    [InlineData("upsert_quest")]
    [InlineData("upsert_lore")]
    [InlineData("upsert_rumor")]
    [InlineData("upsert_plot_thread")]
    [InlineData("upsert_spell")]
    [InlineData("upsert_feat")]
    [InlineData("world_build")]
    public void ToolCallExamples_UpsertRegistryEntries_HaveRetryTemplates(string toolName)
    {
        Assert.True(ToolCallExamples.TryGet(toolName, out var example));
        var (summary, retryExample) = ToolCallExamples.BuildDeserializationErrorResponse(toolName, "boom");

        Assert.Contains("boom", summary);
        Assert.NotNull(retryExample);
        Assert.Equal(toolName, example.ToolName);
    }

    [Fact]
    public void ModelEnumErrorHints_SuggestsSettlementForCityLocationType()
    {
        // Object-rooted path (e.g. "$.type"), as System.Text.Json reports it when deserializing a
        // single upsert entity payload — as opposed to commit's array-rooted "$[0].newState".
        var ex = new JsonException(
            "The JSON value could not be converted to CampaignVault.Models.LocationType.",
            path: "$.type", lineNumber: null, bytePositionInLine: null);
        using var doc = JsonDocument.Parse("""{ "id": "locations/x", "name": "X", "description": "d", "type": "City" }""");

        var enriched = ModelEnumErrorHints.Enrich(ex, doc.RootElement);

        Assert.Contains("Settlement", enriched);
        Assert.Contains("Did you mean 'Settlement'?", enriched);
    }

    [Fact]
    public void RecommendedSystemPrompt_Under12kCharacters()
    {
        var path = Path.Combine(FindRepoRoot(), "recommended-system-prompt.md");
        var content = File.ReadAllText(path);
        var start = content.IndexOf("```text", StringComparison.Ordinal);
        var end = content.IndexOf("```", start + 7, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var prompt = content[(start + 7)..end].Trim();
        Assert.InRange(prompt.Length, 1, 12_000);
        Assert.Contains("world_build", prompt);
        Assert.Contains("do NOT commit HP separately", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisteredToolSchema_UnderChattynessCap()
    {
        // Tracks LLM context cost from tool schema (proxy: descriptions in registered tools).
        // Phase A: retired 11 upsert_* tools, added 3 lightweight (get_session_briefing, get_scene_summary, get_npc_summary).
        // Phase B: added 2 semantic wrappers (travel_to, rest_at_location).
        // Phase C.1-C.4: added take_turn (unified mutation+refresh). 48 → 37 (A1) → 40 (A3+A4) → 42 (B) → 38 (C.2, demoted 4 query tools) → 39 (C.4, added take_turn).
        // Phase C.5: demoted commit to internal (query tool surface reduction). 39 → 38.
        var tools = ToolCatalog.GetAll();
        var schemaSize = tools
            .Sum(t => (t.Name?.Length ?? 0) + (t.Description?.Length ?? 0));

        // Target: 38 registered tools after Phase C.5 (commit demotion).
        // Bound allows reasonable variation during active development.
        Assert.InRange(schemaSize, 1, 50_000);
        Assert.InRange(tools.Count, 35, 50);
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

    /// <summary>
    /// Regression guard for ItemDetails discoverability wording: routes an LLM DM from the
    /// top-level tools it reads first (commit, get_commit_schema) toward
    /// item_update's upsertItemDetail using natural trigger words, without already knowing the
    /// field name. UpsertItem was retired to world_build in Phase A of tool-surface reduction.
    /// See itemdetails-tooling-analysis follow-up.
    /// </summary>
    [Theory]
    [InlineData(typeof(MetaTools), nameof(MetaTools.GetCommitSchema), "upsertItemDetail")]
    public void ToolDescriptions_ContainItemDetailsDiscoverabilityTriggerWords(Type toolType, string methodName, string expectedSubstring)
    {
        var method = toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == methodName && m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() != null);

        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Assert.Contains(expectedSubstring, description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetItem_IsRegisteredAsAnMcpTool()
    {
        var method = typeof(DeepDiveTools).GetMethod(nameof(DeepDiveTools.GetItem), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>());
    }

    [Theory]
    [InlineData(nameof(WorldBuilderTools.UpsertCharacter))]
    [InlineData(nameof(WorldBuilderTools.UpsertLocation))]
    [InlineData(nameof(WorldBuilderTools.UpsertLore))]
    [InlineData(nameof(WorldBuilderTools.UpsertItem))]
    [InlineData(nameof(WorldBuilderTools.UpsertCreature))]
    [InlineData(nameof(WorldBuilderTools.UpsertPlotThread))]
    [InlineData(nameof(WorldBuilderTools.UpsertSpell))]
    [InlineData(nameof(WorldBuilderTools.UpsertFeat))]
    [InlineData(nameof(WorldBuilderTools.UpsertFaction))]
    [InlineData(nameof(WorldBuilderTools.UpsertQuest))]
    [InlineData(nameof(WorldBuilderTools.UpsertRumor))]
    public void RetiredUpsertMethods_AreNotRegisteredAsAnMcpTool(string methodName)
    {
        var method = typeof(WorldBuilderTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>());
    }
}
