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
    [Description("Visual sandbox, items, and knowledge")]
    VisualSandbox,

    /// <summary>Full commit type enum cheat sheet with all discriminators.</summary>
    [Description("Commit type enum reference")]
    CommitEnum,

    /// <summary>Laziness traps, tips, common mistakes.</summary>
    [Description("FAQ and laziness traps")]
    Faq
}

[McpServerToolType]
public class MetaTools : IMcpServerTool
{
    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMMIT SCHEMA: Returns machine-readable metadata for commit $type discriminators — required fields, side effects, and co-commit hints. Call this once at session start or when unsure which $type to use. Filter by category to reduce output.")]
    public Task<ToolResult<IReadOnlyList<CommitTypeSchema>>> GetCommitSchema(
        [Description("Optional commit-schema category filter (groups $type discriminators, not tools): Combat, Narrative, World, PlotThread. Omit to return all. Unrelated to list_tools' category parameter, which groups commit $types instead.")]
        string? category = null)
    {
        var schema = CommitSchemaRegistry.GetAll(category);
        return Task.FromResult(new ToolResult<IReadOnlyList<CommitTypeSchema>>(
            true, schema,
            $"Returned {schema.Count} commit type schemas{(category != null ? $" for category '{category}'" : "")}. Side-effect types are marked hasSideEffects=true — do not duplicate their auto-mutations."));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"TOOL CATALOG: Returns the complete list of CampaignVault MCP tools (name, category, one-line description). Call this if search-based discovery only surfaced a subset. Optional category filter available.")]
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(
        [Description("Optional tool-grouping category filter (groups tools, not $type discriminators). Omit to return all tools. Values: Session & exploration, Mutation & time, Combat & rulesets, Campaign management, Deep dives, World builder, System. Unrelated to get_commit_schema's category parameter, which groups commit $types instead.")] string? category = null)
    {
        var tools = ToolCatalog.GetByCategory(category);
        var summary = string.IsNullOrWhiteSpace(category)
            ? $"Returned {tools.Count} tools across all categories. Call get_help for usage patterns."
            : $"Returned {tools.Count} tools in category '{category.Trim()}'.";
        return Task.FromResult(new ToolResult<IReadOnlyList<ToolCatalogEntry>>(true, tools, summary));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: CALL THIS FIRST (with no topic). Returns lean quickstart + golden rules. For focused deep dives, pass topic: patterns, combat, world-pressure, visual-sandbox, commit-enum, or faq. Each topic is self-contained with copy-paste examples.")]
    public Task<ToolResult<string>> GetHelp(
        [Description("Optional help topic for focused deep-dive sections: 'patterns' (commit examples), 'combat' (ruleset actions), 'world-pressure', 'visual-sandbox', 'commit-enum', 'faq'. Omit for lean quickstart.")]
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

            _ => DmHelpManual.QuickstartSection
        };
    }
}
