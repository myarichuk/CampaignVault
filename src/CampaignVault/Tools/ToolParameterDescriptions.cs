namespace CampaignVault.Tools;

/// <summary>
/// Shared MCP parameter descriptions — single source for tool schemas and get_help alignment.
/// </summary>
internal static class ToolParameterDescriptions
{
    // Repeated on every campaign-scoped tool in tools/list — keep it short.
    public const string CampaignNameRequired = "Campaign slug, e.g. 'dragon-heist'.";

    public const string CampaignSlugRequired = "Campaign slug, e.g. 'dragon-heist' (lowercased, spaces become hyphens).";
}