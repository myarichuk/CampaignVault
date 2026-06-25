namespace CampaignVault.Tools;

/// <summary>
/// Shared MCP parameter descriptions — single source for tool schemas and get_help alignment.
/// </summary>
internal static class ToolParameterDescriptions
{
    public const string CampaignNameRequired =
        "Campaign slug (e.g. 'dragon-heist'; engine canonicalizes spaces to hyphens). Required on every tool call.";

    public const string CampaignNameOptional = CampaignNameRequired; // transitional during phase 3 rollout

    public const string CampaignSlugRequired =
        "Campaign slug (e.g. 'dragon-heist'). Slugs are canonicalized: spaces/underscores become hyphens, lowercase.";
}