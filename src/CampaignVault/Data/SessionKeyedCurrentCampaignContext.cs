namespace CampaignVault.Data;

/// <summary>
/// Resolves the current campaign from the MCP session ID (when present) via <see cref="CampaignSelectionStore"/>.
/// Falls back to a single process-wide slot when no session ID is available (stdio transport, tests).
/// </summary>
public sealed class SessionKeyedCurrentCampaignContext(
    CampaignSelectionStore store,
    IMcpSessionAccessor sessionAccessor) : ICurrentCampaignContext
{
    public string CurrentCampaignName => store.GetCurrent(sessionAccessor.SessionId);

    public bool HasSelection => store.HasSelection(sessionAccessor.SessionId);

    public void SetCurrent(string campaignName) => store.SetCurrent(sessionAccessor.SessionId, campaignName);
}