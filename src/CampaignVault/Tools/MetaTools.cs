using System.ComponentModel;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MetaTools
{
    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMMIT SCHEMA: Returns machine-readable metadata for commit $type discriminators — required fields, side effects, and co-commit hints. Call this once at session start or when unsure which $type to use. Filter by category to reduce output.")]
    public Task<ToolResult<IReadOnlyList<CommitTypeSchema>>> GetCommitSchema(
        [Description("Optional category filter: Combat, Narrative, World, PlotThread, Meta. Omit to return all.")]
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
        [Description("Optional category filter. Omit to return all tools. Values: Session & exploration, Mutation & time, Combat & rulesets, Campaign management, Deep dives, World builder, System.")] string? category = null)
    {
        var tools = ToolCatalog.GetByCategory(category);
        var summary = string.IsNullOrWhiteSpace(category)
            ? $"Returned {tools.Count} tools across all categories. Call get_help for usage patterns."
            : $"Returned {tools.Count} tools in category '{category.Trim()}'.";
        return Task.FromResult(new ToolResult<IReadOnlyList<ToolCatalogEntry>>(true, tools, summary));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: CALL THIS FIRST. Returns the canonical DM manual with quickstart, explicit campaignName scoping (stateless MCP — no session selection), tool index, copy-paste commit patterns, ruleset_actions, StatusEffects, and WorldPressure handling. Use list_tools for the full machine-readable catalog.")]
    public Task<ToolResult<string>> GetHelp()
    {
        var manual = DmHelpManual.Body
            .Replace("{{TOOL_INDEX}}", ToolCatalog.FormatHelpIndex(), StringComparison.Ordinal)
            .Replace("{{COMMIT_TYPES}}", CommitTypesReference.SupportedTypesBullet, StringComparison.Ordinal)
            .Replace("{{CONVERSATION_EXAMPLE}}", CommitHelpExamples.ConversationBatch.Trim(), StringComparison.Ordinal)
            .Replace("{{COMMIT_ENUM_VALUES}}", CommitEnumCheatSheet.Full, StringComparison.Ordinal);

        return Task.FromResult(new ToolResult<string>(true, manual, "Help manual retrieved."));
    }
}
