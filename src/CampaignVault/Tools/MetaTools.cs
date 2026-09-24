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
    Tools,

    /// <summary>take_turn full/delta mode mechanics, reseed triggers, and drift-protection fingerprint reference.</summary>
    [Description("take_turn full/delta mode reference")]
    TakeTurnModes
}

[McpServerToolType]
public class MetaTools : IMcpServerTool
{
    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMMIT SCHEMA: Returns machine-readable metadata for the $type discriminators used inside take_turn's changes[] array — required fields, side effects, and co-commit hints. Call this once at session start or when unsure which $type to use — e.g. for scratches, stains, secret compartments, or other lasting item damage/wear, look at item_update's upsertItemDetail. Filter by category to reduce output. NOTE: individual $type entries don't expose their own minutesElapsed in this schema — use the top-level take_turn request's minutesElapsed field to nudge hunger/thirst/tiredness during an ordinary scene without waiting for rest/advance_world; it applies to the first eligible change in the batch.")]
    public Task<ToolResult<IReadOnlyList<CommitTypeSchema>>> GetCommitSchema(
        [Description("Optional filter over change $type categories: Combat, Narrative, World, PlotThread. Omit (with type also omitted) to get an index of all types (name+category+summary only, no field lists).")]
        string? category = null,
        [Description("Optional single commit $type discriminator to retrieve (e.g. 'hp', 'ruleset_action'). When specified, returns only that variant's full schema.")]
        string? type = null)
    {
        if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(type))
        {
            var index = CommitSchemaRegistry.GetIndex();
            return Task.FromResult(new ToolResult<IReadOnlyList<CommitTypeSchema>>(
                true, index,
                $"Returned an index of all {index.Count} commit types (name/category/summary only — no field lists). " +
                "Call again with category or type to get a specific variant's full schema (required/optional fields, side effects, example). Plugin mode verbs (e.g. crafting_step) are not listed here; look one up with type= once its mode is enabled."));
        }

        var schema = CommitSchemaRegistry.GetAll(category, type);
        return Task.FromResult(new ToolResult<IReadOnlyList<CommitTypeSchema>>(
            true, schema,
            $"Returned {schema.Count} commit type schemas{(type != null ? $" for type '{type}'" : $" for category '{category}'")}. Side-effect types are marked hasSideEffects=true — do not duplicate their auto-mutations."));
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
    [Description(@"REFERENCE LOOKUP. The server pushes what you need automatically on tool responses under `guidance`; follow it and do not call this speculatively. For session-0 setup questions, pass topic: 'onboarding' (guided Q&A — start_campaign_onboarding) or 'world-building' (seeding order). For quick reference: 'commit-enum' (valid $type discriminators), 'tools' (MCP tool catalog), or 'take-turn-modes' (take_turn full/delta mode + drift-protection reference). Guidance on patterns, combat, spells, world-pressure, and item tracking is delivered proactively on tool responses — do not fetch those sections via get_help.")]
    public Task<ToolResult<string>> GetHelp(
        [Description("Optional help topic: 'onboarding' (guided session-0 Q&A), 'world-building' (session-0 seeding order + world_build example), 'commit-enum' (valid change $type discriminators), 'tools' (full MCP tool catalog), 'take-turn-modes' (take_turn full/delta mode mechanics + drift protection), or 'faq' (laziness traps + tips). Omit to get reference-lookup status. Guidance on patterns, combat, spells, world-pressure, and sandbox is delivered on tool responses under `guidance` — do not call get_help for those.")]
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

            HelpTopic.TakeTurnModes => DmHelpManual.TakeTurnModesSection,

            _ => "Reference lookup only. The server pushes what you need automatically on tool responses under `guidance`; follow it and don't call get_help speculatively. For session-0 setup: try topic=onboarding or topic=world-building. For reference: topic=commit-enum or topic=tools. Guidance on patterns, combat, spells, world-pressure, and sandbox is delivered on tool responses — do not fetch those sections here."
        };
    }
}
