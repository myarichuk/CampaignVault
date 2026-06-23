namespace CampaignVault.Data;

/// <summary>
/// Provides the currently selected campaign for the active DI lifetime scope
/// (typically one MCP HTTP request in stateless hosting).
///
/// <see cref="select_campaign"/> sets this value for subsequent tool calls in the same scope.
/// Most campaign-aware tools fall back to this context when no explicit campaignName
/// is passed. Pass <c>campaignName</c> explicitly when clients cannot rely on scope-local state.
/// </summary>
public interface ICurrentCampaignContext
{
    /// <summary>
    /// The name of the currently selected campaign. Returns <see cref="CampaignSelectionStore.UnselectedSentinel"/>
    /// when nothing has been selected yet.
    /// </summary>
    string CurrentCampaignName { get; }

    /// <summary>
    /// Whether <see cref="select_campaign"/> (or <see cref="create_campaign"/>) has established a campaign
    /// for this context's session key.
    /// </summary>
    bool HasSelection { get; }

    /// <summary>
    /// Changes the current campaign for subsequent operations in this context.
    /// </summary>
    void SetCurrent(string campaignName);
}

/// <summary>
/// In-memory implementation of <see cref="ICurrentCampaignContext"/> for tests and direct injection.
/// </summary>
public sealed class CurrentCampaignContext : ICurrentCampaignContext
{
    private string _current = CampaignSelectionStore.UnselectedSentinel;

    public string CurrentCampaignName => _current;

    public bool HasSelection => !string.IsNullOrEmpty(_current);

    public void SetCurrent(string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            campaignName = CampaignSelectionStore.UnselectedSentinel;
        }

        _current = campaignName.Trim().ToLowerInvariant();
    }
}