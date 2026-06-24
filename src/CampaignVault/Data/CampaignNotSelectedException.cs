namespace CampaignVault.Data;

public sealed class CampaignNotSelectedException : Exception
{
    public CampaignNotSelectedException()
        : base(
            "No campaign selected for this MCP session. Call select_campaign (requires Mcp-Session-Id or MCP_SESSION_ID), " +
            "or pass campaignName explicitly on every tool call. " +
            "When MCP_STATELESS=1, session headers are unavailable — use campaignName on each call.")
    {
    }
}