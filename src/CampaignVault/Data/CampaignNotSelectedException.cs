namespace CampaignVault.Data;

public sealed class CampaignNotSelectedException : Exception
{
    public CampaignNotSelectedException()
        : base(
            "No campaign selected for this MCP session. Call select_campaign first, or pass campaignName explicitly. " +
            "When MCP_STATELESS=1, Mcp-Session-Id is unavailable and campaignName is required on every tool call.")
    {
    }
}