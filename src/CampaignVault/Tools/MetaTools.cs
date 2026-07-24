using System.ComponentModel;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

/// <summary>
/// Help topics for focused, paginated manual sections.
/// </summary>
internal enum HelpTopic
{
    /// <summary>Quickstart, golden rules, critical foundations (default, no topic param).</summary>
    [Description("Default")]
    None = 0,

    /// <summary>Initial world-building / session-0 seeding: recommended order, world_build example.</summary>
    [Description("Initial world-building (session 0) guide")]
    WorldBuilding,

    /// <summary>Commit patterns: tavern walkthrough, quest lifecycle, wilderness, transients.</summary>
    [Description("Commit patterns and narrative examples")]
    Patterns,

    /// <summary>Character bootstrap, ruleset actions, combat, spells, status effects.</summary>
    [Description("Combat and ruleset actions")]
    Combat,

    /// <summary>WorldPressure system, pressure contributors, pressure-driven narrative.</summary>
    [Description("World pressure and simulation")]
    WorldPressure,

    /// <summary>Tags, items, appearance, knowledge, physics sandbox.</summary>
    [Description("Visual sandbox: item damage/wear/hidden-feature tracking, tags, appearance, knowledge")]
    VisualSandbox,

    /// <summary>Full commit type enum cheat sheet with all discriminators.</summary>
    [Description("Commit type enum reference")]
    CommitEnum,

    /// <summary>Laziness traps, tips, common mistakes.</summary>
    [Description("FAQ and laziness traps")]
    Faq,

    /// <summary>Full MCP tool catalog grouped by category (absorbed the former list_tools tool).</summary>
    [Description("MCP tool catalog")]
    Tools
}

[McpServerToolType]
public class MetaTools : IMcpServerTool
{
    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMMIT SCHEMA: Returns machine-readable metadata for the $type discriminators used inside take_turn's changes[] array — required fields, side effects, and co-commit hints. Call this once at session start or when unsure which $type to use — e.g. for scratches, stains, secret compartments, or other lasting item damage/wear, look at item_update's upsertItemDetail. Filter by category to reduce output. NOTE: every $type below (except rest/travel, which have their own hour fields) also accepts an optional 'minutesElapsed' field — not listed per-entry since it's universal — to nudge hunger/thirst/tiredness during an ordinary scene without waiting for rest/advance_world.")]
    public Task<ToolResult<IReadOnlyList<CommitTypeSchema>>> GetCommitSchema(
        [Description("Optional filter over change $type categories: Combat, Narrative, World, PlotThread. Omit to return all.")]
        string? category = null)
    {
        var schema = CommitSchemaRegistry.GetAll(category);
        return Task.FromResult(new ToolResult<IReadOnlyList<CommitTypeSchema>>(
            true, schema,
            $"Returned {schema.Count} commit type schemas{(category != null ? $" for category '{category}'" : "")}. Side-effect types are marked hasSideEffects=true — do not duplicate their auto-mutations. Reminder: every type here (except rest/travel) also accepts an optional 'minutesElapsed' to nudge needs during ordinary scenes."));
    }

    internal Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(string? category = null)
    {
        var tools = ToolCatalog.GetByCategory(category);
        var summary = string.IsNullOrWhiteSpace(category)
            ? $"Returned {tools.Count} tools across all categories. Call get_help for usage patterns."
            : $"Returned {tools.Count} tools in category '{category.Trim()}'.";
        return Task.FromResult(new ToolResult<IReadOnlyList<ToolCatalogEntry>>(true, tools, summary));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: CALL THIS FIRST (with no topic). Returns lean quickstart + golden rules. For focused deep dives, pass topic: world-building, patterns, combat, world-pressure, visual-sandbox (item damage/wear/hidden-feature tracking, tags, appearance, knowledge), commit-enum, tools (full MCP tool catalog), or faq. Each topic is self-contained with copy-paste examples.")]
    public Task<ToolResult<string>> GetHelp(
        [Description("Optional help topic for focused deep-dive sections: 'world-building' (session-0 seeding order + world_build example), 'patterns' (take_turn change examples), 'combat' (ruleset actions), 'world-pressure', 'visual-sandbox' (persistent item details: scratches/stains/secret compartments/damage, tags, appearance, knowledge), 'commit-enum', 'tools' (full MCP tool catalog grouped by category), 'faq'. Omit for lean quickstart.")]
        string? topic = null)
    {
        var content = GetHelpContent(topic);
        return Task.FromResult(new ToolResult<string>(true, content, "Help section retrieved."));
    }

    private string GetHelpContent(string? topicStr)
    {
        var normalized = topicStr?.Replace("-", "", StringComparison.Ordinal);
        if (!Enum.TryParse<HelpTopic>(normalized, ignoreCase: true, out var topic))
        {
            topic = HelpTopic.None;
        }

        return topic switch
        {
            HelpTopic.WorldBuilding => DmHelpManual.WorldBuildingSection,

            HelpTopic.Patterns => DmHelpManual.PatternsSection
                .Replace("{{CONVERSATION_EXAMPLE}}", CommitHelpExamples.ConversationBatch.Trim(), StringComparison.Ordinal),

            HelpTopic.Combat => DmHelpManual.CombatSection
                .Replace("{{SPELL_EXAMPLES}}", CommitSpellHelpExamples.HelpSection.Trim(), StringComparison.Ordinal)
                .Replace("{{SPELL_ROUTING}}", CommitSpellHelpExamples.RoutingGuide.Trim(), StringComparison.Ordinal),

            HelpTopic.WorldPressure => DmHelpManual.WorldPressureSection,

            HelpTopic.VisualSandbox => DmHelpManual.VisualSandboxSection,

            HelpTopic.CommitEnum => DmHelpManual.CommitEnumSection
                .Replace("{{COMMIT_ENUM_VALUES}}", CommitEnumCheatSheet.Full, StringComparison.Ordinal),

            HelpTopic.Faq => DmHelpManual.FaqSection,

            HelpTopic.Tools => "## MCP Tool Catalog\n\n" + ToolCatalog.FormatHelpIndex(),

            _ => DmHelpManual.QuickstartSection
        };
    }
}
