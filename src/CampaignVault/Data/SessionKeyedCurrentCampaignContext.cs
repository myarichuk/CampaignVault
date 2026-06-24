namespace CampaignVault.Data;

/// <summary>
/// Resolves the current campaign from the MCP session ID via <see cref="CampaignSelectionStore"/>.
/// Requires a session ID (HTTP header or MCP_SESSION_ID); there is no process-wide fallback.
/// </summary>
public sealed class SessionKeyedCurrentCampaignContext(
    CampaignSelectionStore store,
    IMcpSessionAccessor sessionAccessor) : ICurrentCampaignContext
{
    public string CurrentCampaignName => store.GetCurrent(sessionAccessor.SessionId);

    public bool HasSelection => store.HasSelection(sessionAccessor.SessionId);

    public void SetCurrent(string campaignName) => store.SetCurrent(sessionAccessor.SessionId, campaignName);
}