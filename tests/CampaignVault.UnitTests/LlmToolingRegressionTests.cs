using System;
using System.Collections.Generic;
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

        var modified = ToolCallExamples.TryNormalize("take_turn", args, out var rewrites);

        Assert.True(modified);
        Assert.Contains("rumor.newState(Active)→Nascent", rewrites);
        Assert.Contains("rumor.removed sourceCharacterId", rewrites);

        var change = args["request"]!["changes"]![0]!.AsObject();
        Assert.Equal("rumor", change["$type"]!.GetValue<string>());
        Assert.Equal("Nascent", change["newState"]!.GetValue<string>());
        Assert.False(change.ContainsKey("sourceCharacterId"));

        using var doc = JsonDocument.Parse(args["request"]!["changes"]!.ToJsonString());
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

        ToolCallExamples.TryNormalize("take_turn", args, out _);

        var change = args["request"]!["changes"]![0]!.AsObject();
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
        // Phases A–C.8 took the surface 48 → 35 (upsert retirement, take_turn, commit/query/wrapper demotions).
        // Consolidation phase: merged deep-dives into get_entity, kickoff tools into start_session,
        // combat lifecycle into combat(action), rules lookups into get_rules_reference, list_tools into
        // get_help topic=tools, need descriptors into world_build. 35 → ~15.
        var tools = ToolCatalog.GetAll();
        var schemaSize = tools
            .Sum(t => (t.Name?.Length ?? 0) + (t.Description?.Length ?? 0));

        Assert.InRange(schemaSize, 1, 30_000);
        Assert.InRange(tools.Count, 14, 18);
    }

    private static readonly string[] RetiredToolNames =
    [
        "get_scene", "get_npc_context", "get_scene_summary", "get_npc_summary",
        "get_world_state", "get_party", "get_session_briefing", "get_npc_needs",
        "get_current_campaign", "set_active_system", "set_narrative_focus",
        "get_system_handbook", "get_spells", "query_creatures",
        "get_faction_context", "get_quest_details", "get_plot_thread", "list_plot_threads",
        "get_item", "list_tools", "start_combat", "next_turn", "end_combat", "get_combat",
        "trigger_opportunity_attack", "travel_to", "rest_at_location",
        "define_need_descriptor", "get_need_descriptors",
        // removed commit discriminators
        "character_create", "rumor_create", "quest_create", "location_create",
    ];

    /// <summary>
    /// The exact consolidated public surface. A tool appearing or disappearing must be a deliberate
    /// decision that updates this list, the help manual, the system prompt, and the skills together.
    /// </summary>
    [Fact]
    public void RegisteredToolNames_MatchTheConsolidatedSurfaceExactly()
    {
        var expected = new[]
        {
            "advance_world", "combat", "create_campaign", "end_session", "finalize_campaign_onboarding", "get_commit_schema",
            "get_config", "get_entity", "get_help", "get_rules_reference", "list_campaigns",
            "recall_history", "search_world", "start_campaign_onboarding", "start_session", "submit_onboarding_answer", "take_turn", "world_build",
        };

        var actual = ToolCatalog.GetAll().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// No LLM-visible surface may reference a retired tool name: an LLM following such a reference
    /// would call a tool that does not exist. Scans registered tool descriptions, every get_help
    /// section, the recommended system prompt, and every skill file.
    /// </summary>
    [Fact]
    public void LlmVisibleSurfaces_DoNotReferenceRetiredToolNames()
    {
        var repoRoot = FindRepoRoot();
        var surfaces = new List<(string Source, string Text)>();

        foreach (var tool in ToolCatalog.GetAll())
        {
            surfaces.Add(($"tool description: {tool.Name}", tool.Description ?? ""));
        }

        surfaces.Add(("DmHelpManual.Quickstart", CampaignVault.Tools.DmHelpManual.QuickstartSection));
        surfaces.Add(("DmHelpManual.Patterns", CampaignVault.Tools.DmHelpManual.PatternsSection));
        surfaces.Add(("DmHelpManual.Combat", CampaignVault.Tools.DmHelpManual.CombatSection));
        surfaces.Add(("DmHelpManual.WorldPressure", CampaignVault.Tools.DmHelpManual.WorldPressureSection));
        surfaces.Add(("DmHelpManual.VisualSandbox", CampaignVault.Tools.DmHelpManual.VisualSandboxSection));
        surfaces.Add(("DmHelpManual.CommitEnum", CampaignVault.Tools.DmHelpManual.CommitEnumSection));
        surfaces.Add(("DmHelpManual.Faq", CampaignVault.Tools.DmHelpManual.FaqSection));
        surfaces.Add(("DmHelpManual.WorldBuilding", CampaignVault.Tools.DmHelpManual.WorldBuildingSection));

        surfaces.Add(("recommended-system-prompt.md",
            File.ReadAllText(Path.Combine(repoRoot, "recommended-system-prompt.md"))));

        foreach (var skill in Directory.EnumerateFiles(Path.Combine(repoRoot, "claude_skills"), "SKILL.md", SearchOption.AllDirectories))
        {
            surfaces.Add((Path.GetRelativePath(repoRoot, skill), File.ReadAllText(skill)));
        }

        var violations = new List<string>();
        foreach (var (source, text) in surfaces)
        {
            foreach (var retired in RetiredToolNames)
            {
                // Word-ish boundary: avoid matching inside a longer identifier (e.g. get_item inside get_item_details).
                var idx = 0;
                while ((idx = text.IndexOf(retired, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var before = idx == 0 ? ' ' : text[idx - 1];
                    var afterIdx = idx + retired.Length;
                    var after = afterIdx >= text.Length ? ' ' : text[afterIdx];
                    if (!char.IsLetterOrDigit(before) && before != '_' && !char.IsLetterOrDigit(after) && after != '_')
                    {
                        violations.Add($"{source}: '{retired}'");
                        break;
                    }

                    idx = afterIdx;
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Retired tool names referenced in LLM-visible surfaces:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void EverySkillFile_HasYamlFrontmatterWithNameAndDescription()
    {
        var repoRoot = FindRepoRoot();
        foreach (var skill in Directory.EnumerateFiles(Path.Combine(repoRoot, "claude_skills"), "SKILL.md", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(skill);
            Assert.True(content.StartsWith("---", StringComparison.Ordinal),
                $"{skill} is missing YAML frontmatter (required for skill discovery).");
            Assert.Contains("name:", content[..content.IndexOf("---", 3, StringComparison.Ordinal)], StringComparison.Ordinal);
            Assert.Contains("description:", content[..content.IndexOf("---", 3, StringComparison.Ordinal)], StringComparison.Ordinal);
        }
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
    public void GetEntity_IsRegisteredAsAnMcpTool_AndFetchersAreDemoted()
    {
        var getEntity = typeof(DeepDiveTools).GetMethod(nameof(DeepDiveTools.GetEntity), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotNull(getEntity);
        Assert.NotNull(getEntity!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>());

        // The per-type fetchers it replaced must stay internal and unregistered.
        foreach (var name in new[] { "GetItem", "GetFactionContext", "GetQuestDetails", "GetPlotThread", "ListPlotThreads" })
        {
            var method = typeof(DeepDiveTools).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>());
        }
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
