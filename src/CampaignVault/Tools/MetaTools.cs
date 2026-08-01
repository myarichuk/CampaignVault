using System.ComponentModel;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

/// <summary>
/// Help topics for focused, paginated manual sections.
/// Large topical sections (patterns, combat, spells, world-pressure, visual-sandbox, quickstart) are now
/// delivered as push-based guidance hints on tool responses instead of via get_help, reducing speculative
/// pull-based fetching. This enum carries only session-0 procedural guidance, reference, and FAQ.
/// </summary>
internal enum HelpTopic
{
    /// <summary>Reference lookup only. The server pushes what you need automatically on tool responses; call this only to look up something you were not told.</summary>
    [Description("Reference lookup")]
    None = 0,

    /// <summary>Initial world-building / session-0 seeding: recommended order, world_build example.</summary>
    [Description("Initial world-building (session 0) guide")]
    WorldBuilding,

    /// <summary>Guided session-0 Q&amp;A flow: start_campaign_onboarding / submit_onboarding_answer / finalize_campaign_onboarding — when to use it vs going straight to create_campaign + world_build.</summary>
    [Description("Guided campaign onboarding (session 0 Q&A) — when to use it vs create_campaign")]
    Onboarding,

    /// <summary>Full commit type enum cheat sheet with all discriminators.</summary>
    [Description("Commit type enum reference")]
    CommitEnum,

    /// <summary>Laziness traps, tips, common mistakes.</summary>
    [Description("FAQ and laziness traps")]
    Faq,

    /// <summary>Full MCP tool catalog grouped by category.</summary>
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
        string? category = null,
        [Description("Optional single commit $type discriminator to retrieve (e.g. 'hp', 'ruleset_action'). When specified, returns only that variant.")]
        string? type = null)
    {
        var schema = CommitSchemaRegistry.GetAll(category, type);
        return Task.FromResult(new ToolResult<IReadOnlyList<CommitTypeSchema>>(
            true, schema,
            $"Returned {schema.Count} commit type schemas{(type != null ? $" for type '{type}'" : category != null ? $" for category '{category}'" : "")}. Side-effect types are marked hasSideEffects=true — do not duplicate their auto-mutations. Reminder: every type here (except rest/travel) also accepts an optional 'minutesElapsed' to nudge needs during ordinary scenes."));
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
    [Description(@"REFERENCE LOOKUP. The server pushes what you need automatically on tool responses under `guidance`; follow it and do not call this speculatively. For session-0 setup questions, pass topic: 'onboarding' (guided Q&A — start_campaign_onboarding) or 'world-building' (seeding order). For quick reference: 'commit-enum' (valid $type discriminators) or 'tools' (MCP tool catalog). Guidance on patterns, combat, spells, world-pressure, and item tracking is delivered proactively on tool responses — do not fetch those sections via get_help.")]
    public Task<ToolResult<string>> GetHelp(
        [Description("Optional help topic: 'onboarding' (guided session-0 Q&A), 'world-building' (session-0 seeding order + world_build example), 'commit-enum' (valid change $type discriminators), 'tools' (full MCP tool catalog), or 'faq' (laziness traps + tips). Omit to get reference-lookup status. Guidance on patterns, combat, spells, world-pressure, and sandbox is delivered on tool responses under `guidance` — do not call get_help for those.")]
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
            HelpTopic.Onboarding => DmHelpManual.OnboardingSection,

            HelpTopic.WorldBuilding => DmHelpManual.WorldBuildingSection,

            HelpTopic.CommitEnum => DmHelpManual.CommitEnumSection
                .Replace("{{COMMIT_ENUM_VALUES}}", CommitEnumCheatSheet.Full, StringComparison.Ordinal),

            HelpTopic.Faq => DmHelpManual.FaqSection,

            HelpTopic.Tools => "## MCP Tool Catalog\n\n" + ToolCatalog.FormatHelpIndex(),

            _ => "Reference lookup only. The server pushes what you need automatically on tool responses under `guidance`; follow it and don't call get_help speculatively. For session-0 setup: try topic=onboarding or topic=world-building. For reference: topic=commit-enum or topic=tools. Guidance on patterns, combat, spells, world-pressure, and sandbox is delivered on tool responses — do not fetch those sections here."
        };
    }
}
