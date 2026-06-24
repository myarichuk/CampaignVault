namespace CampaignVault.Data;

/// <summary>
/// Thrown when a campaign-scoped operation requires an MCP session ID but none is available.
/// </summary>
public sealed class CampaignSessionRequiredException : Exception
{
    public const string Guidance =
        "No MCP session ID is available. For HTTP clients, send the Mcp-Session-Id header (disabled when MCP_STATELESS=1). " +
        "For stdio or local CLI, set the MCP_SESSION_ID environment variable. " +
        "Alternatively, pass campaignName explicitly on every tool call.";

    public CampaignSessionRequiredException()
        : base($"Campaign selection requires an MCP session. {Guidance}")
    {
    }
}