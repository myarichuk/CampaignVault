namespace CampaignVault.Tools;

/// <summary>
/// Shared MCP parameter descriptions — single source for tool schemas and get_help alignment.
/// </summary>
internal static class ToolParameterDescriptions
{
    public const string CampaignNameOptional =
        "Optional campaign slug (e.g. 'dragon-heist'; engine canonicalizes spaces to hyphens). " +
        "When omitted, uses the campaign selected via select_campaign for this MCP session. " +
        "Session identity: Mcp-Session-Id HTTP header, or MCP_SESSION_ID env (stdio/local CLI). " +
        "When MCP_STATELESS=1 or no session is available, pass campaignName on every tool call.";

    public const string CampaignSlugRequired =
        "Campaign slug (e.g. 'dragon-heist'). Slugs are canonicalized: spaces/underscores become hyphens, lowercase.";
}